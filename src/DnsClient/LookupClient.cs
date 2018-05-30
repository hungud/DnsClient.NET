﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DnsClient.Protocol.Options;

namespace DnsClient
{
    /// <summary>
    /// The <see cref="LookupClient"/> is the main query class of this library and should be used for any kind of DNS lookup query.
    /// <para>
    /// It implements <see cref="ILookupClient"/> and <see cref="IDnsQuery"/> which contains a number of extension methods, too.
    /// The extension methods internally all invoke the standard <see cref="IDnsQuery"/> queries though.
    /// </para>
    /// </summary>
    /// <seealso cref="IDnsQuery"/>
    /// <seealso cref="ILookupClient"/>
    /// <example>
    /// A basic example wihtout specifying any DNS server, which will use the DNS server configured by your local network.
    /// <code>
    /// <![CDATA[
    /// var client = new LookupClient();
    /// var result = client.Query("google.com", QueryType.A);
    ///
    /// foreach (var aRecord in result.Answers.ARecords())
    /// {
    ///     Console.WriteLine(aRecord);
    /// }
    /// ]]>
    /// </code>
    /// </example>
    public class LookupClient : ILookupClient, IDnsQuery
    {
        private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan s_infiniteTimeout = System.Threading.Timeout.InfiniteTimeSpan;
        private static readonly TimeSpan s_maxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);
        private static readonly int s_serverHealthCheckInterval = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;
        private static int _uniqueId = 0;
        private bool _healthCheckRunning = false;
        private int _lastHealthCheck = 0;
        private readonly ResponseCache _cache = new ResponseCache(true);

        ////private readonly object _endpointLock = new object();
        private readonly DnsMessageHandler _messageHandler;

        private readonly DnsMessageHandler _tcpFallbackHandler;
        private readonly ConcurrentQueue<NameServer> _endpoints;
        private readonly Random _random = new Random();
        private TimeSpan _timeout = s_defaultTimeout;

        /// <summary>
        /// Gets or sets a flag indicating whether Tcp should be used in case a Udp response is truncated.
        /// Default is <c>True</c>.
        /// <para>
        /// If <c>False</c>, truncated results will potentially yield no answers.
        /// </para>
        /// </summary>
        public bool UseTcpFallback { get; set; } = true;

        /// <summary>
        /// Gets or sets a flag indicating whether Udp should not be used at all.
        /// Default is <c>False</c>.
        /// <para>
        /// Enable this only if Udp cannot be used because of your firewall rules for example.
        /// </para>
        /// </summary>
        public bool UseTcpOnly { get; set; }

        /// <summary>
        /// Gets the list of configured name servers.
        /// </summary>
        public IReadOnlyCollection<NameServer> NameServers { get; }

        /// <summary>
        /// If enabled, each <see cref="IDnsQueryResponse"/> will contain a full documentation of the response(s).
        /// Default is <c>False</c>.
        /// </summary>
        /// <seealso cref="IDnsQueryResponse.AuditTrail"/>
        public bool EnableAuditTrail { get; set; } = false;

        /// <summary>
        /// Gets or sets a flag indicating whether DNS queries should instruct the DNS server to do recursive lookups, or not.
        /// Default is <c>True</c>.
        /// </summary>
        /// <value>The flag indicating if recursion should be used or not.</value>
        public bool Recursion { get; set; } = true;

        /// <summary>
        /// Gets or sets the number of tries to get a response from one name server before trying the next one.
        /// Only transient errors, like network or connection errors will be retried.
        /// Default is <c>5</c>.
        /// <para>
        /// If all configured <see cref="NameServers"/> error out after retries, an exception will be thrown at the end.
        /// </para>
        /// </summary>
        /// <value>The number of retries.</value>
        public int Retries { get; set; } = 5;

        /// <summary>
        /// Gets or sets a flag indicating whether the <see cref="ILookupClient"/> should throw a <see cref="DnsResponseException"/>
        /// in case the query result has a <see cref="DnsResponseCode"/> other than <see cref="DnsResponseCode.NoError"/>.
        /// Default is <c>False</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If set to <c>False</c>, the query will return a result with an <see cref="IDnsQueryResponse.ErrorMessage"/>
        /// which contains more information.
        /// </para>
        /// <para>
        /// If set to <c>True</c>, any query method of <see cref="IDnsQuery"/> will throw an <see cref="DnsResponseException"/> if
        /// the response header indicates an error.
        /// </para>
        /// <para>
        /// If both, <see cref="ContinueOnDnsError"/> and <see cref="ThrowDnsErrors"/> are set to <c>True</c>,
        /// <see cref="ILookupClient"/> will continue to query all configured <see cref="NameServers"/>.
        /// If none of the servers yield a valid response, a <see cref="DnsResponseException"/> will be thrown
        /// with the error of the last response.
        /// </para>
        /// </remarks>
        /// <seealso cref="DnsResponseCode"/>
        /// <seealso cref="ContinueOnDnsError"/>
        public bool ThrowDnsErrors { get; set; } = false;

        /// <summary>
        /// Gets or sets the request timeout in milliseconds. <see cref="Timeout"/> is used for limiting the connection and request time for one operation.
        /// Timeout must be greater than zero and less than <see cref="int.MaxValue"/>.
        /// If <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> (or -1) is used, no timeout will be applied.
        /// </summary>
        /// <remarks>
        /// If a very short timeout is configured, queries will more likely result in <see cref="TimeoutException"/>s.
        /// <para>
        /// Important to note, <see cref="TimeoutException"/>s will be retried, if <see cref="Retries"/> are not disabled (set to <c>0</c>).
        /// This should help in case one or more configured DNS servers are not reachable or under load for example.
        /// </para>
        /// </remarks>
        public TimeSpan Timeout
        {
            get { return _timeout; }
            set
            {
                if ((value <= TimeSpan.Zero || value > s_maxTimeout) && value != s_infiniteTimeout)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _timeout = value;
            }
        }

        /// <summary>
        /// Gets or sets a flag indicating if the <see cref="LookupClient"/> should use response caching or not.
        /// The cache duration is calculated by the resource record of the response. Usually, the lowest TTL is used.
        /// Default is <c>True</c>.
        /// </summary>
        /// <remarks>
        /// In case the DNS Server returns records with a TTL of zero. The response cannot be cached.
        /// Setting <see cref="MinimumCacheTimeout"/> can overwrite this behavior and cache those responses anyways for at least the given duration.
        /// </remarks>
        public bool UseCache
        {
            get
            {
                return _cache.Enabled;
            }
            set
            {
                _cache.Enabled = value;
            }
        }

        /// <summary>
        /// Gets or sets a <see cref="TimeSpan"/> which can override the TTL of a resource record in case the
        /// TTL of the record is lower than this minimum value.
        /// Default is <c>Null</c>.
        /// <para>
        /// This is useful in cases where the server retruns records with zero TTL.
        /// </para>
        /// </summary>
        /// <remarks>
        /// This setting gets igonred in case <see cref="UseCache"/> is set to <c>False</c>.
        /// </remarks>
        public TimeSpan? MinimumCacheTimeout
        {
            get
            {
                return _cache.MinimumTimout;
            }
            set
            {
                _cache.MinimumTimout = value;
            }
        }

        /// <summary>
        /// Gets or sets a flag indicating whether the <see cref="ILookupClient"/> can cycle through all
        /// configured <see cref="NameServers"/> on each consecutive request, basically using a random server, or not.
        /// Default is <c>True</c>.
        /// If only one <see cref="NameServer"/> is configured, this setting is not used.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If <c>False</c>, configured endpoint will be used in random order.
        /// If <c>True</c>, the order will be preserved.
        /// </para>
        /// <para>
        /// Even if <see cref="UseRandomNameServer"/> is set to <c>True</c>, the endpoint might still get
        /// disabled and might not being used for some time if it errors out, e.g. no connection can be established.
        /// </para>
        /// </remarks>
        public bool UseRandomNameServer { get; set; } = true;

        /// <summary>
        /// Gets or sets a flag indicating whether to query the next configured <see cref="NameServers"/> in case the response of the last query
        /// returned a <see cref="DnsResponseCode"/> other than <see cref="DnsResponseCode.NoError"/>.
        /// Default is <c>True</c>.
        /// </summary>
        /// <remarks>
        /// If <c>True</c>, lookup client will continue until a server returns a valid result, or,
        /// if no <see cref="NameServers"/> yield a valid result, the last response with the error will be returned.
        /// In case no server yields a valid result and <see cref="ThrowDnsErrors"/> is also enabled, an exception
        /// will be thrown containing the error of the last response.
        /// </remarks>
        /// <seealso cref="ThrowDnsErrors"/>
        public bool ContinueOnDnsError { get; set; } = true;

        /// <summary>
        /// Creates a new instance of <see cref="LookupClient"/> without specifying any name server.
        /// This will implicitly use the name server(s) configured by the local network adapter.
        /// </summary>
        /// <remarks>
        /// This uses <see cref="NameServer.ResolveNameServers(bool, bool)"/>.
        /// The resulting list of name servers is highly dependent on the local network configuration and OS.
        /// </remarks>
        /// <example>
        /// In the following example, we will create a new <see cref="LookupClient"/> without explicitly defining any DNS server.
        /// This will use the DNS server configured by your local network.
        /// <code>
        /// <![CDATA[
        /// var client = new LookupClient();
        /// var result = client.Query("google.com", QueryType.A);
        ///
        /// foreach (var aRecord in result.Answers.ARecords())
        /// {
        ///     Console.WriteLine(aRecord);
        /// }
        /// ]]>
        /// </code>
        /// </example>
        public LookupClient()
            : this(NameServer.ResolveNameServers()?.ToArray())
        {
        }

        /// <summary>
        /// Creates a new instance of <see cref="LookupClient"/> with one or more DNS servers identified by their <see cref="IPAddress"/>.
        /// The default port <c>53</c> will be used for all <see cref="IPAddress"/>s provided.
        /// </summary>
        /// <param name="nameServers">The <see cref="IPAddress"/>(s) to be used by this <see cref="LookupClient"/> instance.</param>
        /// <example>
        /// To connect to one or more DNS server using the default port, we can use this overload:
        /// <code>
        /// <![CDATA[
        /// // configuring the client to use google's public IPv4 DNS servers.
        /// var client = new LookupClient(IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4"));
        /// ]]>
        /// </code>
        /// </example>
        public LookupClient(params IPAddress[] nameServers)
            : this(nameServers?.Select(p => new IPEndPoint(p, NameServer.DefaultPort)).ToArray())
        {
        }

        /// <summary>
        /// Create a new instance of <see cref="LookupClient"/> with one DNS server defined by <paramref name="address"/> and <paramref name="port"/>.
        /// </summary>
        /// <param name="address">The <see cref="IPAddress"/> of the DNS server.</param>
        /// <param name="port">The port of the DNS server.</param>
        /// <example>
        /// In case you want to connect to one specific DNS server which does not run on the default port <c>53</c>, you can do so like in the following example:
        /// <code>
        /// <![CDATA[
        /// var client = new LookupClient(IPAddress.Parse("127.0.0.1"), 8600);
        /// ]]>
        /// </code>
        /// </example>
        public LookupClient(IPAddress address, int port)
           : this(new IPEndPoint(address, port))
        {
        }

        /// <summary>
        /// Creates a new instance of <see cref="LookupClient"/> with one or more <see cref="IPAddress"/> and port combination
        /// stored in <see cref="IPEndPoint"/>(s).
        /// </summary>
        /// <param name="nameServers">The <see cref="IPEndPoint"/>(s) to be used by this <see cref="LookupClient"/> instance.</param>
        /// <example>
        /// In this example, we instantiate a new <see cref="IPEndPoint"/> using an <see cref="IPAddress"/> and custom port which is different than the default port <c>53</c>.
        /// <code>
        /// <![CDATA[
        /// // Using localhost and port 8600 to connect to a Consul agent.
        /// var endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8600);
        /// var client = new LookupClient(endpoint);
        /// ]]>
        /// </code>
        /// <para>
        /// The <see cref="NameServer"/> class also contains pre defined <see cref="IPEndPoint"/>s for the public google DNS servers, which can be used as follows:
        /// <code>
        /// <![CDATA[
        /// var client = new LookupClient(NameServer.GooglePublicDns, NameServer.GooglePublicDnsIPv6);
        /// ]]>
        /// </code>
        /// </para>
        /// </example>
        public LookupClient(params IPEndPoint[] nameServers)
            : this(nameServers?.Select(p => new NameServer(p))?.ToArray())
        {
        }

        // adding this one for unit testing
        internal LookupClient(params NameServer[] nameServers)
        {
            if (nameServers == null || nameServers.Length == 0)
            {
                throw new ArgumentException("At least one name server must be configured.", nameof(nameServers));
            }

            NameServers = nameServers;

            _endpoints = new ConcurrentQueue<NameServer>(NameServers);
            _messageHandler = new DnsUdpMessageHandler(true);
            _tcpFallbackHandler = new DnsTcpMessageHandler();
        }

        /// <summary>
        /// Does a reverse lookup of the <paramref name="ipAddress"/>.
        /// </summary>
        /// <param name="ipAddress">The <see cref="IPAddress"/>.</param>
        /// <returns>
        /// The <see cref="IDnsQueryResponse" /> which should contain the <see cref="DnsClient.Protocol.PtrRecord"/>.
        /// </returns>
        public IDnsQueryResponse QueryReverse(IPAddress ipAddress)
        {
            if (ipAddress == null)
            {
                throw new ArgumentNullException(nameof(ipAddress));
            }

            var arpa = ipAddress.GetArpaName();
            return Query(arpa, QueryType.PTR, QueryClass.IN);
        }

        /// <summary>
        /// Does a reverse lookup of the <paramref name="ipAddress"/>.
        /// </summary>
        /// <param name="ipAddress">The <see cref="IPAddress"/>.</param>
        /// <returns>
        /// The <see cref="IDnsQueryResponse" /> which should contain the <see cref="DnsClient.Protocol.PtrRecord"/>.
        /// </returns>
        /// <remarks>
        /// The behavior of the query can be controlled by the properties of this <see cref="LookupClient"/> instance.
        /// <see cref="Recursion"/> for example can be disabled and would instruct the DNS server to return no additional records.
        /// </remarks>
        public Task<IDnsQueryResponse> QueryReverseAsync(IPAddress ipAddress)
            => QueryReverseAsync(ipAddress, CancellationToken.None);

        /// <summary>
        /// Does a reverse lookup of the <paramref name="ipAddress" />.
        /// </summary>
        /// <param name="ipAddress">The <see cref="IPAddress" />.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The <see cref="IDnsQueryResponse" /> which should contain the <see cref="DnsClient.Protocol.PtrRecord" />.
        /// </returns>
        /// <remarks>
        /// The behavior of the query can be controlled by the properties of this <see cref="LookupClient"/> instance.
        /// <see cref="Recursion"/> for example can be disabled and would instruct the DNS server to return no additional records.
        /// </remarks>
        public Task<IDnsQueryResponse> QueryReverseAsync(IPAddress ipAddress, CancellationToken cancellationToken)
        {
            if (ipAddress == null)
            {
                throw new ArgumentNullException(nameof(ipAddress));
            }

            var arpa = ipAddress.GetArpaName();
            return QueryAsync(arpa, QueryType.PTR, QueryClass.IN, cancellationToken);
        }

        /// <summary>
        /// Performs a DNS lookup for <paramref name="query" /> and <paramref name="queryType" />.
        /// </summary>
        /// <param name="query">The domain name query.</param>
        /// <param name="queryType">The <see cref="QueryType" />.</param>
        /// <returns>
        /// The <see cref="IDnsQueryResponse" /> which contains the response headers and lists of resource records.
        /// </returns>
        /// <remarks>
        /// The behavior of the query can be controlled by the properties of this <see cref="LookupClient"/> instance.
        /// <see cref="Recursion"/> for example can be disabled and would instruct the DNS server to return no additional records.
        /// </remarks>
        public IDnsQueryResponse Query(string query, QueryType queryType)
            => Query(query, queryType, QueryClass.IN);

        /// <summary>
        /// Performs a DNS lookup for <paramref name="query" />, <paramref name="queryType" /> and <paramref name="queryClass"/>.
        /// </summary>
        /// <param name="query">The domain name query.</param>
        /// <param name="queryType">The <see cref="QueryType" />.</param>
        /// <param name="queryClass">The <see cref="QueryClass"/>.</param>
        /// <returns>
        /// The <see cref="IDnsQueryResponse" /> which contains the response headers and lists of resource records.
        /// </returns>
        /// <remarks>
        /// The behavior of the query can be controlled by the properties of this <see cref="LookupClient"/> instance.
        /// <see cref="Recursion"/> for example can be disabled and would instruct the DNS server to return no additional records.
        /// </remarks>
        public IDnsQueryResponse Query(string query, QueryType queryType, QueryClass queryClass)
            => Query(new DnsQuestion(query, queryType, queryClass));

        private IDnsQueryResponse Query(DnsQuestion question)
        {
            if (question == null)
            {
                throw new ArgumentNullException(nameof(question));
            }

            var head = new DnsRequestHeader(GetNextUniqueId(), Recursion, DnsOpCode.Query);
            var request = new DnsRequestMessage(head, question);
            var handler = UseTcpOnly ? _tcpFallbackHandler : _messageHandler;

            if (_cache.Enabled)
            {
                var cacheKey = ResponseCache.GetCacheKey(question);
                var item = _cache.Get(cacheKey);
                if (item == null)
                {
                    item = ResolveQuery(handler, request);
                    _cache.Add(cacheKey, item);
                }

                return item;
            }
            else
            {
                return ResolveQuery(handler, request);
            }
        }

        // making it internal for unit testing
        internal IDnsQueryResponse ResolveQuery(DnsMessageHandler handler, DnsRequestMessage request, Audit continueAudit = null)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var audit = continueAudit ?? new Audit();
            var servers = GetNextServers();

            DnsResponseException lastDnsResponseException = null;
            Exception lastException = null;
            DnsQueryResponse lastQueryResponse = null;

            foreach (var serverInfo in servers)
            {
                var tries = 0;
                do
                {
                    tries++;
                    lastDnsResponseException = null;
                    lastException = null;

                    try
                    {
                        if (EnableAuditTrail)
                        {
                            audit.StartTimer();
                        }

                        DnsResponseMessage response = handler.Query(serverInfo.Endpoint, request, Timeout);

                        if (response.Header.ResultTruncated && UseTcpFallback && !handler.GetType().Equals(typeof(DnsTcpMessageHandler)))
                        {
                            if (EnableAuditTrail)
                            {
                                audit.AuditTruncatedRetryTcp();
                            }

                            return ResolveQuery(_tcpFallbackHandler, request, audit);
                        }

                        if (EnableAuditTrail)
                        {
                            audit.AuditResolveServers(_endpoints.Count);
                            audit.AuditResponseHeader(response.Header);
                        }

                        if (response.Header.ResponseCode != DnsResponseCode.NoError && EnableAuditTrail)
                        {
                            audit.AuditResponseError(response.Header.ResponseCode);
                        }

                        HandleOptRecords(audit, serverInfo, response);

                        DnsQueryResponse queryResponse = response.AsQueryResponse(serverInfo.Clone());

                        if (EnableAuditTrail)
                        {
                            audit.AuditResponse(queryResponse);
                            audit.AuditEnd(queryResponse);
                            queryResponse.AuditTrail = audit.Build();
                        }

                        serverInfo.Enabled = true;
                        serverInfo.LastSuccessfulRequest = request;
                        lastQueryResponse = queryResponse;

                        if (response.Header.ResponseCode != DnsResponseCode.NoError &&
                            (ThrowDnsErrors || ContinueOnDnsError))
                        {
                            throw new DnsResponseException(response.Header.ResponseCode);
                        }

                        return queryResponse;
                    }
                    catch (DnsResponseException ex)
                    {
                        ////audit.AuditException(ex);
                        ex.AuditTrail = audit.Build();
                        lastDnsResponseException = ex;

                        if (ContinueOnDnsError)
                        {
                            break; // don't retry this server, response was kinda valid
                        }

                        throw ex;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported)
                    {
                        // this socket error might indicate the server endpoint is actually bad and should be ignored in future queries.
                        DisableServer(serverInfo);
                        break;
                    }
                    catch (Exception ex) when (
                        ex is TimeoutException
                        || handler.IsTransientException(ex)
                        || ex is OperationCanceledException
                        || ex is TaskCanceledException)
                    {
                        DisableServer(serverInfo);
                        continue;
                        // retrying the same server...
                    }
                    catch (Exception ex)
                    {
                        DisableServer(serverInfo);

                        audit.AuditException(ex);

                        lastException = ex;

                        // not retrying the same server, use next or return
                        break;
                    }
                } while (tries <= Retries && serverInfo.Enabled);

                if (servers.Count > 1)
                {
                    audit.AuditRetryNextServer();
                }
            }

            if (lastDnsResponseException != null && ThrowDnsErrors)
            {
                throw lastDnsResponseException;
            }

            if (lastQueryResponse != null)
            {
                return lastQueryResponse;
            }

            if (lastException != null)
            {
                throw new DnsResponseException(DnsResponseCode.Unassigned, "Unhandled exception", lastException)
                {
                    AuditTrail = audit.Build()
                };
            }

            throw new DnsResponseException(DnsResponseCode.ConnectionTimeout, $"No connection could be established to any of the following name servers: {string.Join(", ", NameServers)}.")
            {
                AuditTrail = audit.Build()
            };
        }

        /// <summary>
        /// Performs a DNS lookup for <paramref name="query" /> and <paramref name="queryType" />.
        /// </summary>
        /// <param name="query">The domain name query.</param>
        /// <param name="queryType">The <see cref="QueryType" />.</param>
        /// <returns>
        /// The <see cref="IDnsQueryResponse" /> which contains the response headers and lists of resource records.
        /// </returns>
        /// <remarks>
        /// The behavior of the query can be controlled by the properties of this <see cref="LookupClient"/> instance.
        /// <see cref="Recursion"/> for example can be disabled and would instruct the DNS server to return no additional records.
        /// </remarks>
        public Task<IDnsQueryResponse> QueryAsync(string query, QueryType queryType)
            => QueryAsync(query, queryType, CancellationToken.None);

        /// <summary>
        /// Performs a DNS lookup for <paramref name="query" /> and <paramref name="queryType" />.
        /// </summary>
        /// <param name="query">The domain name query.</param>
        /// <param name="queryType">The <see cref="QueryType" />.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The <see cref="IDnsQueryResponse" /> which contains the response headers and lists of resource records.
        /// </returns>
        /// <remarks>
        /// The behavior of the query can be controlled by the properties of this <see cref="LookupClient"/> instance.
        /// <see cref="Recursion"/> for example can be disabled and would instruct the DNS server to return no additional records.
        /// </remarks>
        public Task<IDnsQueryResponse> QueryAsync(string query, QueryType queryType, CancellationToken cancellationToken)
            => QueryAsync(query, queryType, QueryClass.IN, cancellationToken);

        /// <summary>
        /// Performs a DNS lookup for <paramref name="query" />, <paramref name="queryType" /> and <paramref name="queryClass"/>.
        /// </summary>
        /// <param name="query">The domain name query.</param>
        /// <param name="queryType">The <see cref="QueryType" />.</param>
        /// <param name="queryClass">The <see cref="QueryClass"/>.</param>
        /// <returns>
        /// The <see cref="IDnsQueryResponse" /> which contains the response headers and lists of resource records.
        /// </returns>
        /// <remarks>
        /// The behavior of the query can be controlled by the properties of this <see cref="LookupClient"/> instance.
        /// <see cref="Recursion"/> for example can be disabled and would instruct the DNS server to return no additional records.
        /// </remarks>
        public Task<IDnsQueryResponse> QueryAsync(string query, QueryType queryType, QueryClass queryClass)
            => QueryAsync(query, queryType, queryClass, CancellationToken.None);

        /// <summary>
        /// Performs a DNS lookup for <paramref name="query" />, <paramref name="queryType" /> and <paramref name="queryClass" />.
        /// </summary>
        /// <param name="query">The domain name query.</param>
        /// <param name="queryType">The <see cref="QueryType" />.</param>
        /// <param name="queryClass">The <see cref="QueryClass" />.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The <see cref="IDnsQueryResponse" /> which contains the response headers and lists of resource records.
        /// </returns>
        /// <remarks>
        /// The behavior of the query can be controlled by the properties of this <see cref="LookupClient"/> instance.
        /// <see cref="Recursion"/> for example can be disabled and would instruct the DNS server to return no additional records.
        /// </remarks>
        public Task<IDnsQueryResponse> QueryAsync(string query, QueryType queryType, QueryClass queryClass, CancellationToken cancellationToken)
            => QueryAsync(new DnsQuestion(query, queryType, queryClass), cancellationToken);

        private async Task<IDnsQueryResponse> QueryAsync(DnsQuestion question, CancellationToken cancellationToken, bool useCache = true)
        {
            if (question == null)
            {
                throw new ArgumentNullException(nameof(question));
            }

            var head = new DnsRequestHeader(GetNextUniqueId(), Recursion, DnsOpCode.Query);
            var request = new DnsRequestMessage(head, question);
            var handler = UseTcpOnly ? _tcpFallbackHandler : _messageHandler;

            if (_cache.Enabled && useCache)
            {
                var cacheKey = ResponseCache.GetCacheKey(question);
                var item = _cache.Get(cacheKey);
                if (item == null)
                {
                    item = await ResolveQueryAsync(handler, request, cancellationToken).ConfigureAwait(false);
                    _cache.Add(cacheKey, item);
                }

                return item;
            }
            else
            {
                return await ResolveQueryAsync(handler, request, cancellationToken).ConfigureAwait(false);
            }
        }

        // internal for unit testing
        internal Task<IDnsQueryResponse> ResolveQueryAsync(DnsMessageHandler handler, DnsRequestMessage request, CancellationToken cancellationToken, Audit continueAudit = null)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var audit = continueAudit ?? new Audit();
            var servers = GetNextServers();

            return ResolveQueryAsync(handler, request, audit, servers, cancellationToken);
        }

        private async Task<IDnsQueryResponse> ResolveQueryAsync(DnsMessageHandler handler, DnsRequestMessage request, Audit audit, IReadOnlyCollection<NameServer> servers, CancellationToken cancellationToken)
        {
            DnsResponseException lastDnsResponseException = null;
            Exception lastException = null;
            DnsQueryResponse lastQueryResponse = null;

            foreach (var serverInfo in servers)
            {
                var tries = 0;
                do
                {
                    tries++;
                    lastDnsResponseException = null;
                    lastException = null;

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (EnableAuditTrail)
                        {
                            audit.StartTimer();
                        }

                        DnsResponseMessage response;
                        Action onCancel = () => { };
                        Task<DnsResponseMessage> resultTask = handler.QueryAsync(serverInfo.Endpoint, request, cancellationToken, (cancel) =>
                        {
                            onCancel = cancel;
                        });

                        if (Timeout != s_infiniteTimeout || (cancellationToken != CancellationToken.None && cancellationToken.CanBeCanceled))
                        {
                            var cts = new CancellationTokenSource(Timeout);
                            CancellationTokenSource linkedCts = null;
                            if (cancellationToken != CancellationToken.None)
                            {
                                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
                            }
                            using (cts)
                            using (linkedCts)
                            {
                                response = await resultTask.WithCancellation((linkedCts ?? cts).Token, onCancel).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            response = await resultTask.ConfigureAwait(false);
                        }

                        if (response.Header.ResultTruncated && UseTcpFallback && !handler.GetType().Equals(typeof(DnsTcpMessageHandler)))
                        {
                            if (EnableAuditTrail)
                            {
                                audit.AuditTruncatedRetryTcp();
                            }

                            return await ResolveQueryAsync(_tcpFallbackHandler, request, cancellationToken, audit).ConfigureAwait(false);
                        }

                        if (EnableAuditTrail)
                        {
                            audit.AuditResolveServers(_endpoints.Count);
                            audit.AuditResponseHeader(response.Header);
                        }

                        if (response.Header.ResponseCode != DnsResponseCode.NoError && EnableAuditTrail)
                        {
                            audit.AuditResponseError(response.Header.ResponseCode);
                        }

                        HandleOptRecords(audit, serverInfo, response);

                        DnsQueryResponse queryResponse = response.AsQueryResponse(serverInfo.Clone());

                        if (EnableAuditTrail)
                        {
                            audit.AuditResponse(queryResponse);
                            audit.AuditEnd(queryResponse);
                            queryResponse.AuditTrail = audit.Build();
                        }

                        // got a valid result, lets enabled the server again if it was disabled
                        serverInfo.Enabled = true;
                        lastQueryResponse = queryResponse;
                        serverInfo.LastSuccessfulRequest = request;

                        if (response.Header.ResponseCode != DnsResponseCode.NoError &&
                            (ThrowDnsErrors || ContinueOnDnsError))
                        {
                            throw new DnsResponseException(response.Header.ResponseCode);
                        }

                        return queryResponse;
                    }
                    catch (DnsResponseException ex)
                    {
                        ex.AuditTrail = audit.Build();
                        lastDnsResponseException = ex;

                        if (ContinueOnDnsError)
                        {
                            break; // don't retry this server, response was kinda valid
                        }

                        throw;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported)
                    {
                        // this socket error might indicate the server endpoint is actually bad and should be ignored in future queries.
                        DisableServer(serverInfo);
                        break;
                    }
                    catch (Exception ex) when (
                        ex is TimeoutException timeoutEx
                        || handler.IsTransientException(ex)
                        || ex is OperationCanceledException
                        || ex is TaskCanceledException)
                    {
                        // user's token got canceled, throw right away...
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        DisableServer(serverInfo);
                    }
                    catch (Exception ex)
                    {
                        DisableServer(serverInfo);

                        if (ex is AggregateException agg)
                        {
                            agg.Handle((e) =>
                            {
                                if (e is TimeoutException
                                    || handler.IsTransientException(e)
                                    || e is OperationCanceledException
                                    || e is TaskCanceledException)
                                {
                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        throw new OperationCanceledException(cancellationToken);
                                    }

                                    return true;
                                }

                                return false;
                            });
                        }

                        audit.AuditException(ex);
                        lastException = ex;

                        // try next server (this is actually a change and is not configurable, but should be a good thing I guess)
                        break;
                    }
                } while (tries <= Retries && !cancellationToken.IsCancellationRequested && serverInfo.Enabled);

                if (servers.Count > 1)
                {
                    audit.AuditRetryNextServer();
                }
            }

            if (lastDnsResponseException != null && ThrowDnsErrors)
            {
                throw lastDnsResponseException;
            }

            if (lastQueryResponse != null)
            {
                return lastQueryResponse;
            }

            if (lastException != null)
            {
                throw new DnsResponseException(DnsResponseCode.Unassigned, "Unhandled exception", lastException)
                {
                    AuditTrail = audit.Build()
                };
            }

            throw new DnsResponseException(DnsResponseCode.ConnectionTimeout, $"No connection could be established to any of the following name servers: {string.Join(", ", NameServers)}.")
            {
                AuditTrail = audit.Build()
            };
        }

        private void HandleOptRecords(Audit audit, NameServer serverInfo, DnsResponseMessage response)
        {
            var opt = response.Additionals.OfType<OptRecord>().FirstOrDefault();
            if (opt != null)
            {
                if (EnableAuditTrail)
                {
                    audit.AuditOptPseudo();
                }

                serverInfo.SupportedUdpPayloadSize = opt.UdpSize;

                // TODO: handle opt records and remove them later
                response.Additionals.Remove(opt);

                if (EnableAuditTrail)
                {
                    audit.AuditEdnsOpt(opt.UdpSize, opt.Version, opt.ResponseCodeEx);
                }
            }
        }

        private IReadOnlyCollection<NameServer> GetNextServers()
        {
            IReadOnlyCollection<NameServer> servers = null;
            if (_endpoints.Count > 1)
            {
                servers = _endpoints.Where(p => p.Enabled).ToArray();

                // if all servers are disabled, retry all of them
                if (servers.Count == 0)
                {
                    servers = _endpoints.ToArray();
                }

                // shuffle servers only if we do not have to preserve the order
                if (UseRandomNameServer)
                {
                    if (_endpoints.TryDequeue(out NameServer server))
                    {
                        _endpoints.Enqueue(server);
                    }
                }

                RunHealthCheck();
            }
            else
            {
                servers = _endpoints.ToArray();
            }

            return servers;
        }

        private void RunHealthCheck()
        {
            // TickCount jump every 25days to int.MinValue, adjusting...
            var currentTicks = Environment.TickCount & int.MaxValue;
            if (_lastHealthCheck + s_serverHealthCheckInterval < 0 || currentTicks + s_serverHealthCheckInterval < 0) _lastHealthCheck = 0;
            if (!_healthCheckRunning && _lastHealthCheck + s_serverHealthCheckInterval < currentTicks)
            {
                _lastHealthCheck = currentTicks;

                var source = new CancellationTokenSource(TimeSpan.FromMinutes(1));

                Task.Factory.StartNew(
                    state => DoHealthCheck((CancellationToken)state),
                    source.Token,
                    source.Token,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
        }

        private async Task DoHealthCheck(CancellationToken cancellationToken)
        {
            _healthCheckRunning = true;

            foreach (var server in NameServers)
            {
                if (!server.Enabled && server.LastSuccessfulRequest != null)
                {
                    try
                    {
                        var result = await QueryAsync(server.LastSuccessfulRequest.Question, cancellationToken, useCache: false);
                    }
                    catch { }
                }
            }

            _healthCheckRunning = false;
        }

        private void DisableServer(NameServer server)
        {
            if (NameServers.Count > 1)
            {
                server.Enabled = false;
            }
        }

        private ushort GetNextUniqueId()
        {
            if (_uniqueId == ushort.MaxValue || _uniqueId == 0)
            {
                _uniqueId = (ushort)_random.Next(ushort.MaxValue / 2);
            }

            return unchecked((ushort)Interlocked.Increment(ref _uniqueId));
        }

        internal class Audit
        {
            private static readonly int s_printOffset = -32;
            private StringBuilder _auditWriter = new StringBuilder();
            private Stopwatch _swatch;

            public Audit()
            {
            }

            public void StartTimer()
            {
                _swatch = Stopwatch.StartNew();
                _swatch.Restart();
            }

            public void AuditResolveServers(int count)
            {
                _auditWriter.AppendLine($"; ({count} server found)");
            }

            public string Build()
            {
                return _auditWriter.ToString();
            }

            public void AuditTruncatedRetryTcp()
            {
                _auditWriter.AppendLine(";; Truncated, retrying in TCP mode.");
                _auditWriter.AppendLine();
            }

            public void AuditResponseError(DnsResponseCode responseCode)
            {
                _auditWriter.AppendLine($";; ERROR: {DnsResponseCodeText.GetErrorText(responseCode)}");
            }

            public void AuditOptPseudo()
            {
                _auditWriter.AppendLine(";; OPT PSEUDOSECTION:");
            }

            public void AuditResponseHeader(DnsResponseHeader header)
            {
                _auditWriter.AppendLine(";; Got answer:");
                _auditWriter.AppendLine(header.ToString());
                if (header.RecursionDesired && !header.RecursionAvailable)
                {
                    _auditWriter.AppendLine(";; WARNING: recursion requested but not available");
                }
                _auditWriter.AppendLine();
            }

            public void AuditEdnsOpt(short udpSize, byte version, DnsResponseCode responseCodeEx)
            {
                // TODO: flags
                _auditWriter.AppendLine($"; EDNS: version: {version}, flags:; udp: {udpSize}");
            }

            public void AuditResponse(IDnsQueryResponse queryResponse)
            {
                if (queryResponse.Questions.Count > 0)
                {
                    _auditWriter.AppendLine(";; QUESTION SECTION:");
                    foreach (var question in queryResponse.Questions)
                    {
                        _auditWriter.AppendLine(question.ToString(s_printOffset));
                    }
                    _auditWriter.AppendLine();
                }

                if (queryResponse.Answers.Count > 0)
                {
                    _auditWriter.AppendLine(";; ANSWER SECTION:");
                    foreach (var answer in queryResponse.Answers)
                    {
                        _auditWriter.AppendLine(answer.ToString(s_printOffset));
                    }
                    _auditWriter.AppendLine();
                }

                if (queryResponse.Authorities.Count > 0)
                {
                    _auditWriter.AppendLine(";; AUTHORITIES SECTION:");
                    foreach (var auth in queryResponse.Authorities)
                    {
                        _auditWriter.AppendLine(auth.ToString(s_printOffset));
                    }
                    _auditWriter.AppendLine();
                }

                if (queryResponse.Additionals.Count > 0)
                {
                    _auditWriter.AppendLine(";; ADDITIONALS SECTION:");
                    foreach (var additional in queryResponse.Additionals)
                    {
                        _auditWriter.AppendLine(additional.ToString(s_printOffset));
                    }
                    _auditWriter.AppendLine();
                }
            }

            public void AuditEnd(DnsQueryResponse queryResponse)
            {
                var elapsed = _swatch.ElapsedMilliseconds;
                _auditWriter.AppendLine($";; Query time: {elapsed} msec");
                _auditWriter.AppendLine($";; SERVER: {queryResponse.NameServer.Endpoint.Address}#{queryResponse.NameServer.Endpoint.Port}");
                _auditWriter.AppendLine($";; WHEN: {DateTime.UtcNow.ToString("ddd MMM dd HH:mm:ss K yyyy", CultureInfo.InvariantCulture)}");
                _auditWriter.AppendLine($";; MSG SIZE  rcvd: {queryResponse.MessageSize}");
            }

            public void AuditException(Exception ex)
            {
                var aggEx = ex as AggregateException;
                if (ex is DnsResponseException dnsEx)
                {
                    _auditWriter.AppendLine($";; Error: {DnsResponseCodeText.GetErrorText(dnsEx.Code)} {dnsEx.InnerException?.Message ?? dnsEx.Message}");
                }
                else if (aggEx != null)
                {
                    _auditWriter.AppendLine($";; Error: {aggEx.InnerException?.Message ?? aggEx.Message}");
                }
                else
                {
                    _auditWriter.AppendLine($";; Error: {ex.Message}");
                }

                if (Debugger.IsAttached)
                {
                    _auditWriter.AppendLine(ex.ToString());
                }
            }

            public void AuditRetryNextServer()
            {
                _auditWriter.AppendLine();
                _auditWriter.AppendLine("; Trying next server.");
            }
        }
    }
}