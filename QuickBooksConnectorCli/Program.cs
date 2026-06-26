using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using QuickBooksConnectorCore;

namespace QuickBooksConnectorCli
{
    internal static class Program
    {
        private static readonly JavaScriptSerializer JsonSerializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 100
        };

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    throw new CliUsageException("Operation is required. Supported operations: ping, submit-quote.");
                }

                string operation = args[0].Trim().ToLowerInvariant();
                switch (operation)
                {
                    case "ping":
                        return RunPing();

                    case "submit-quote":
                        return RunSubmitQuote();

                    default:
                        throw new CliUsageException($"Unsupported operation '{args[0]}'. Supported operations: ping, submit-quote.");
                }
            }
            catch (CliUsageException ex)
            {
                Console.Error.WriteLine(ex.Message);
                WriteErrorJson(2, ex.Message);
                return 2;
            }
            catch (QuoteRequestParseException ex)
            {
                Console.Error.WriteLine(ex.Message);
                WriteErrorJson(2, ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                WriteErrorJson(1, ex.Message);
                return 1;
            }
        }

        private static int RunPing()
        {
            using (var connection = new QuickBooksServiceConnection())
            {
                string reply = connection.Client.Ping();
                WriteJson(new Dictionary<string, object>
                {
                    { "status", "ok" },
                    { "reply", reply }
                });
            }

            return 0;
        }

        private static int RunSubmitQuote()
        {
            string input = Console.In.ReadToEnd();
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new CliUsageException("submit-quote requires a QBQuoteUploadRequest JSON payload on stdin.");
            }

            // Same parse -> SubmitQuote -> serialize pipeline the localhost bridge uses.
            Console.Out.WriteLine(SubmitQuoteHandler.Handle(input));
            return 0;
        }

        private static void WriteJson(object value)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(value));
        }

        private static void WriteErrorJson(int statusCode, string statusMessage)
        {
            WriteJson(new Dictionary<string, object>
            {
                { "StatusCode", statusCode == 0 ? 1 : statusCode },
                { "StatusMessage", statusMessage },
                { "Data", null }
            });
        }

        private class CliUsageException : Exception
        {
            internal CliUsageException(string message)
                : base(message)
            {
            }
        }
    }
}
