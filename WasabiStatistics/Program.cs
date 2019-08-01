using NBitcoin;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace WasabiStatistics
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            await SmartbitTools.GetAddresses(); // If you have the transactions this can be commented out.
            await SmartbitTools.MakeStatistic();
        }
    }
}
