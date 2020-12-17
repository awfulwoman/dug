using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DnsClient;
using dug.Data;
using dug.Data.Models;
using dug.Utils;

namespace dug.Services
{
    public class DnsQueryService : IDnsQueryService
    {
        private async Task<IDnsQueryResponse> QueryDnsServer(DnsServer server, string url, QueryType queryType, TimeSpan timeout, int retries = 0){
            LookupClientOptions options = new LookupClientOptions(new IPAddress[] {server.IPAddress}) {
                Timeout = timeout,
                Retries = retries,
            };
            
            var client = new LookupClient(options);
            return await client.QueryAsync(url, queryType);
        }

        //This is unused but is cool and i might want to use it when importing server data that doesnt say whether or not it has DNSSEC?
        public async Task<bool> ServerHasDNSSEC(IPAddress serverAddress, TimeSpan timeout, int retries = 0){
            LookupClientOptions options = new LookupClientOptions(new IPAddress[] {serverAddress}) {
                Timeout = timeout,
                Retries = retries,
                RequestDnsSecRecords = true
            };
            var client = new LookupClient(options);
            var response = await client.QueryAsync("www.dnssec-tools.org", QueryType.ANY);
            return response.Header.IsAuthenticData;
        }

        public async Task<Dictionary<DnsServer, List<DnsResponse>>> QueryServers(string url, IEnumerable<DnsServer> dnsServers, TimeSpan timeout, IEnumerable<QueryType> queryTypes, int retries = 0)
        {
            ConcurrentDictionary<DnsServer, List<DnsResponse>> results = new ConcurrentDictionary<DnsServer, List<DnsResponse>>();

            var serverTasks = dnsServers.Select(async server => {
                var queryTasks = queryTypes.Select(async queryType => {
                    Stopwatch clock = new Stopwatch();
                    try{
                        DugConsole.VerboseWriteLine($"START -- {server.IPAddress}");
                        clock.Start();
                        var queryResult = await QueryDnsServer(server, url, queryType, timeout, retries);
                        long responseTime = clock.ElapsedMilliseconds;
                        DugConsole.VerboseWriteLine($"FINISH -- {server.IPAddress} -- {responseTime}");
                        var response = new DnsResponse(queryResult, responseTime, queryType);
                        results.AddOrUpdate(server,
                            (serv) => new List<DnsResponse>{response},
                            (serv, list) => {
                                list.Add(response);
                                return list;
                            });
                    }
                    catch (DnsResponseException dnsException){ //TODO: There is an issue where ThrowDnsErrors isnt respected, so i have to catch them and deal with it... https://github.com/MichaCo/DnsClient.NET/issues/99
                        long responseTime = clock.ElapsedMilliseconds;
                        var response = new DnsResponse(dnsException, responseTime, queryType);
                        results.AddOrUpdate(server,
                            (serv) => new List<DnsResponse>{response},
                            (serv, list) => {
                                list.Add(response);
                                return list;
                            });
                        if(dnsException.Code == DnsResponseCode.ConnectionTimeout){
                            DugConsole.VerboseWriteLine($"TIMEOUT -- {server.IPAddress} -- {responseTime}");
                            return;
                        }
                        DugConsole.VerboseWriteLine($"ERROR -- {server.IPAddress} -- {responseTime}");
                    }
                    catch{
                        DugConsole.VerboseWriteLine($"UNHANDLED ERROR -- {server.IPAddress} -- {clock.ElapsedMilliseconds}");
                    }
                });
                await Task.WhenAll(queryTasks);
            });

            await Task.WhenAll(serverTasks);

            Console.WriteLine($"Finished, got {results.Select(pair => pair.Value.Count(res => !res.HasError)).Sum()} good responses out of {dnsServers.Count() * queryTypes.Count()} requests");

            return new Dictionary<DnsServer, List<DnsResponse>>(results);
        }
    }
}