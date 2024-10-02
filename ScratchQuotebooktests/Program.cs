using System;
using System.Collections.Generic;
using System.Diagnostics;
using QBRequestLibrary; // Ensure this matches your actual namespace
using QuickBooksIPCContracts;

namespace ScratchQuotebooktests
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Starting AllItemNonInvQueryRequest Test...");

            try
            {
                // Initialize the Stopwatch to measure execution time
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();

                // Instantiate the AllItemNonInvQueryRequest
                var allItemQueryRequest = new AllItemNonInvQueryRequest();

                // Optionally, set any necessary parameters
                // For example: allItemQueryRequest.Set(someValue);

                // Send the request to QuickBooks
                QBStatusResponse<List<QBItem>> response = allItemQueryRequest.SendRequest();

                stopwatch.Stop();

                // Check the response status
                if (response.StatusCode >= 0)
                {
                    Console.WriteLine($"Successfully retrieved {response.Data.Count} non-inventory items.");
                    Console.WriteLine($"Execution Time: {stopwatch.ElapsedMilliseconds} ms.\n");

                    // Display the first 10 items as a sample
                    Console.WriteLine("Sample Items:");
                    Console.WriteLine("-------------------------------");
                    int i = 0;
                    foreach (var item in response.Data)
                    {
                        if (i >= 10) break;
                        Console.WriteLine($"Item {i + 1}:");
                        Console.WriteLine($"  Number      : {item.Number}");
                        Console.WriteLine($"  Description : {item.Description}\n");
                        i++;
                    }
                }
                else
                {
                    Console.WriteLine($"Failed to retrieve items. Status Code: {response.StatusCode}");
                    Console.WriteLine($"Status Message: {response.StatusMessage}");
                }
            }
            catch (QBRequestLibraryRuntimeError ex)
            {
                // Handle specific QBRequestLibrary runtime errors
                Console.WriteLine($"Runtime Error: {ex.Message}");
            }
            catch (InvalidResponseException ex)
            {
                // Handle invalid response errors
                Console.WriteLine($"Invalid Response: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Handle any other unforeseen exceptions
                Console.WriteLine($"An unexpected error occurred: {ex.Message}");
            }

            Console.WriteLine("\nTest Completed. Press any key to exit.");
            Console.ReadKey();
        }
    }
}
