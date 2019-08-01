using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace WasabiStatistics
{
    public class SmartbitTools
    {
        private const string BaseAddress = "https://api.smartbit.com.au/v1/";
        private const string CoordinatorAddress = "bc1qs604c7jv6amk4cxqlnvuxv26hv3e48cds4m0ew";

        private static readonly string RoamingFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static string StatisticFolder = Path.Combine(RoamingFolder, "WasabiStatistics");
        private static string TransactionsFolder = Path.Combine(StatisticFolder, "Transactions");
        private static string ResultsFolder = Path.Combine(StatisticFolder, "Results");

        public static async Task GetAddresses()
        {
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            string address = CoordinatorAddress;
            using (var client = new HttpClient()
            {
                BaseAddress = new Uri(BaseAddress)
            })
            {
                string nextLink = $"blockchain/address/{address}/?limit=10"; // Using the
                var getListTask = client.GetAsync(nextLink);
                int transactionTotalCount = 0;
                int transactionCount = 0;
                while (!string.IsNullOrWhiteSpace(nextLink))
                {
                    try
                    {
                        Console.WriteLine("Fetching transactions...");
                        using (HttpResponseMessage response = await getListTask)
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                throw new Exception(response.ReasonPhrase);
                            }
                            Console.WriteLine("Processing transactions...");
                            string cont = await response.Content.ReadAsStringAsync();
                            var json = JObject.Parse(cont);

                            var isFirst = string.IsNullOrEmpty(json["address"]["transaction_paging"]["prev"]?.ToString());
                            if (isFirst)
                            {
                                int.TryParse(json["address"]["total"]["transaction_count"].ToString(), out transactionTotalCount);
                            }

                            nextLink = json["address"]["transaction_paging"]["next_link"]?.ToString();

                            getListTask = client.GetAsync(nextLink);

                            JToken jsonTransactions = json["address"]["transactions"];
                            //foreach (var transaction in jsonTransactions.Where(x => x["txid"].ToString() != "175388de0864b4d7d81f27a9a5ef7e347aea7d28988b8ff14d09fe4cbb1639cc")) // BUGGY TX
                            Console.WriteLine("Saving transactions to file...");
                            foreach (var transaction in jsonTransactions)
                            {
                                transactionCount++;
                                if (int.TryParse(transaction["confirmations"].ToString(), out int confirmations))
                                {
                                    if (confirmations == 0) continue;
                                }

                                if (int.TryParse(transaction["block"].ToString(), out int block))
                                {
                                    Console.WriteLine($"block: {block}");
                                }

                                try
                                {
                                    await SaveTransaction(transaction);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Problem while processing tx: {transaction?.ToString()}, {ex.Message}");
                                }
                            }
                        }
                        Console.WriteLine($"Process: {transactionCount}/{transactionTotalCount}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Problem while processing fetch: {nextLink}, {ex.Message}");
                    }
                }
            }
            Console.WriteLine($"Got all transactions in {stopWatch.Elapsed.TotalSeconds} secs.");
        }

        private static async Task SaveTransaction(JToken transaction)
        {
            if (!Directory.Exists(TransactionsFolder))
            {
                Directory.CreateDirectory(TransactionsFolder);
            }

            var txid = transaction["txid"].ToString();

            var filePath = Path.Combine(TransactionsFolder, $"{txid}.json");

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            using (StreamWriter sw = new StreamWriter(filePath))
            using (var rJsonTextWriter = new JsonTextWriter(sw))
            {
                await transaction.WriteToAsync(rJsonTextWriter);
            }
        }

        public static async Task MakeStatistic()
        {
            if (!Directory.Exists(ResultsFolder))
            {
                Directory.CreateDirectory(ResultsFolder);
            }

            using (var package = new ExcelPackage())
            {
                ExcelWorksheet addressReusesWorksheet = package.Workbook.Worksheets.Add("AddressReuses");

                addressReusesWorksheet.Cells[1, 1].Value = "Year.Month";
                addressReusesWorksheet.Cells[1, 2].Value = "Type";
                addressReusesWorksheet.Cells[1, 3].Value = "Address";
                addressReusesWorksheet.Cells[1, 4].Value = "Txid";

                ExcelWorksheet transactionsWorksheet = package.Workbook.Worksheets.Add("Transactions");
                transactionsWorksheet.Cells[1, 1].Value = "Year.Month";
                transactionsWorksheet.Cells[1, 2].Value = "Txid";
                transactionsWorksheet.Cells[1, 3].Value = "BaseAnonset";
                transactionsWorksheet.Cells[1, 4].Value = "BaseDenomination";
                transactionsWorksheet.Cells[1, 5].Value = "inputSideReuse";
                transactionsWorksheet.Cells[1, 6].Value = "outputSideReuse";
                transactionsWorksheet.Cells[1, 7].Value = "inputOutputSideReuse";
                transactionsWorksheet.Cells[1, 8].Value = "in-outReuse % BaseAnon";

                HashSet<string> allInputs = new HashSet<string>();
                HashSet<string> allOutputs = new HashSet<string>();

                foreach (var tx in Directory.GetFiles(TransactionsFolder))
                {
                    HashSet<string> txInputs = new HashSet<string>();
                    HashSet<string> txOutputs = new HashSet<string>();

                    var content = await File.ReadAllTextAsync(tx);
                    JObject transaction = JObject.Parse(content);

                    if (int.Parse(transaction["input_count"].ToString()) < 10 || int.Parse(transaction["output_count"].ToString()) < 10)
                    {
                        continue; // This is not a CoinJoin.
                    }

                    // Working on a specific transaction

                    DateTime dateTime = default;
                    if (int.TryParse(transaction["time"].ToString(), out int time))
                    {
                        DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(time);
                        dateTime = dateTimeOffset.UtcDateTime;
                    }

                    int inputSideReuse = 0;
                    int outputSideReuse = 0;
                    int inputOutputSideReuse = 0;

                    void AddLineToAddressReuses(string typeName, string address)
                    {
                        var index = addressReusesWorksheet.Dimension.Rows + 1;
                        addressReusesWorksheet.Cells[index, 1].Value = $"{dateTime.Year}.{dateTime.Month}";
                        addressReusesWorksheet.Cells[index, 2].Value = typeName;
                        addressReusesWorksheet.Cells[index, 3].Value = address;
                        addressReusesWorksheet.Cells[index, 4].Value = transaction["txid"].ToString();
                    };

                    void AddLineToTransactions(ulong baseDenomination, int baseAnonset)
                    {
                        var index = transactionsWorksheet.Dimension.Rows + 1;
                        transactionsWorksheet.Cells[index, 1].Value = $"{dateTime.Year}.{dateTime.Month.ToString("00")}";
                        transactionsWorksheet.Cells[index, 2].Value = transaction["txid"].ToString();
                        transactionsWorksheet.Cells[index, 3].Value = baseAnonset;
                        transactionsWorksheet.Cells[index, 4].Value = baseDenomination;
                        transactionsWorksheet.Cells[index, 5].Value = inputSideReuse;
                        transactionsWorksheet.Cells[index, 6].Value = outputSideReuse;
                        transactionsWorksheet.Cells[index, 7].Value = inputOutputSideReuse;
                        transactionsWorksheet.Cells[index, 8].Value = ((float)(inputOutputSideReuse * 100) / baseAnonset);
                    };

                    foreach (var input in transaction["inputs"].SelectMany(x => x["addresses"]))
                    {
                        try
                        {
                            var address = input.ToString();

                            if (txInputs.Contains(address))
                            {
                                // Input address reuse!
                                AddLineToAddressReuses("InputTx", address);
                                inputSideReuse++;
                            }
                            else
                            {
                                txInputs.Add(address);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Problem while processing input: {input?.ToString()}, {ex.Message}");
                        }
                    }

                    foreach (var output in transaction["outputs"].SelectMany(x => x["addresses"]))
                    {
                        try
                        {
                            var address = output.ToString();
                            if (txOutputs.Contains(address))
                            {
                                AddLineToAddressReuses("OutputTx", address);
                                outputSideReuse++;
                            }
                            else
                            {
                                if (address != CoordinatorAddress)
                                {
                                    txOutputs.Add(address);
                                }
                            }

                            if (txInputs.Contains(address))
                            {
                                AddLineToAddressReuses("InputOutputTx", address);
                                inputOutputSideReuse++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Problem while processing input: {output?.ToString()}, {ex.Message}");
                        }
                    }

                    var outputAmounts = transaction["outputs"].Select(x => ulong.Parse(x["value_int"].ToString()));
                    var maxAnonset = outputAmounts.GroupBy(x => x).OrderByDescending(x => x.Count()).FirstOrDefault();

                    AddLineToTransactions(maxAnonset.Key, maxAnonset.Count());

                    // Working on all CoinJoins.

                    foreach (var input in txInputs)
                    {
                        var address = input.ToString();
                        if (allInputs.Contains(input))
                        {
                            AddLineToAddressReuses("InputGlobal", address);
                        }
                        else
                        {
                            allInputs.Add(input);
                        }
                    }

                    foreach (var output in txOutputs)
                    {
                        var address = output.ToString();
                        if (allOutputs.Contains(output))
                        {
                            AddLineToAddressReuses("OutputGlobal", address);
                        }
                        else
                        {
                            if (address != CoordinatorAddress)
                            {
                                allOutputs.Add(output);
                            }
                        }
                    }
                }

                // Excel Table settings.

                addressReusesWorksheet.Cells[addressReusesWorksheet.Dimension.ToString()].AutoFilter = true;
                addressReusesWorksheet.Cells[addressReusesWorksheet.Dimension.ToString()].AutoFitColumns(0);

                transactionsWorksheet.Cells[transactionsWorksheet.Dimension.ToString()].AutoFilter = true;
                transactionsWorksheet.Cells[transactionsWorksheet.Dimension.ToString()].AutoFitColumns(0);

                // Save the result to disk.

                var fileName = Path.Combine(ResultsFolder, $"WasabiResult{DateTime.Now.ToString("yyyyMMdd_HHmmss")}.xlsx");
                using (FileStream fs = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                {
                    package.SaveAs(fs);
                }
            }
        }
    }
}
