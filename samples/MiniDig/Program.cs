﻿using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DigApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var loggerFactory = new LoggerFactory().WithFilter(new FilterLoggerSettings()
            {
                { "Default", LogLevel.Information }
            });
            //loggerFactory.AddConsole();

            var logFilename = $"Log/dig.log";

            if (File.Exists(logFilename))
            {
                File.Delete(logFilename);
            }

            loggerFactory.AddSerilog(new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.File(logFilename)
                .CreateLogger());

            var app = new CommandLineApplication(throwOnUnexpectedArg: true);

            app.Command("perf", (perfApp) => new PerfCommand(perfApp, loggerFactory, args), throwOnUnexpectedArg: true);
            app.Command("random", (randApp) => new RandomCommand(randApp, loggerFactory, args), throwOnUnexpectedArg: true);

            var defaultCommand = new DigCommand(app, loggerFactory, args);

            try
            {
                app.Execute(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}