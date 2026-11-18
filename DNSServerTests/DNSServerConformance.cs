/*
 * Copyright (c) 2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of DNSServer <https://github.com/OpenChargingCloud/DNSServer>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Net;
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.DNSServer.Tests
{

    /// <summary>
    /// What every node has to answer, asked of a DNS server - the conformance
    /// suite of WWCP_Node_TestKit - and the name server's own JSON API, which
    /// the web interface is drawn from.
    /// </summary>
    /// <remarks>
    /// DNS listens on the loopback address only, on ports the system chose, so
    /// that the suite's nodes do not fight over 53.
    /// </remarks>
    public class DNSServerConformance : NodeConformanceTests
    {

        protected override WWCPNode NewNode(String   Directory,
                                            JObject  Configuration)
            => Make(Directory, Configuration, null);

        protected override WWCPNode? NewNodeWritingLogFiles(String   Directory,
                                                            JObject  Configuration,
                                                            String   LogPath)
            => Make(Directory, Configuration, LogPath);

        private static DNSServerNode Make(String   Directory,
                                          JObject  Configuration,
                                          String?  LogPath)
        {

            if (Configuration[DNSServerSettings.SectionName] is not JObject)
                Configuration[DNSServerSettings.SectionName] = new JObject(
                                                                   new JProperty("listen", "127.0.0.1")
                                                               );

            var file = Path.Combine(Directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, Configuration.ToString());

            return new DNSServerNode(
                       HTTPPort:          IPPort.Parse(TestPorts.Free()),
                       AccountsPath:      Path.Combine(Directory, "accounts"),
                       ConfigFile:        new WWCPConfigFile(file),
                       CertificatesPath:  Path.Combine(Directory, "certificates"),
                       LogToConsole:      false,
                       LogPath:           LogPath,
                       BridgeDebugLog:    false,
                       CommandLine:       settings => settings with { Port = IPPort.Zero }
                   );

        }


        #region TheRecordsAreChangedOverTheJSONAPI()

        /// <summary>
        /// An A record added and removed by whoever may edit the name server:
        /// answered with what was said and the records as they are now - and
        /// what is no address refused, and nothing to remove said so.
        /// </summary>
        [Test]
        public async Task TheRecordsAreChangedOverTheJSONAPI()
        {

            using var http          = await SignedIn();

            var (added, answer)     = await Send(http, HttpMethod.Post, "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "www.example.org"),
                                                             new JProperty("address",  "192.0.2.10"),
                                                             new JProperty("ttl",      "10m")));

            Assert.That(added,                                         Is.EqualTo(HttpStatusCode.OK), answer.ToString());
            Assert.That(answer["said"]!.Value<String>(),               Does.Contain("192.0.2.10"));
            Assert.That(answer["records"]!.Count(),                    Is.EqualTo(1));
            Assert.That(answer["records"]![0]!["name"]!.Value<String>(), Is.EqualTo("www.example.org."));
            Assert.That(answer["records"]![0]!["type"]!.Value<String>(), Is.EqualTo("A"));
            Assert.That(answer["records"]![0]!["ttl"]!.Value<Int64>(),   Is.EqualTo(600));
            Assert.That(answer["records"]![0]!["data"]!.Value<String>(), Is.EqualTo("192.0.2.10"));

            var records             = JArray.Parse(await http.GetStringAsync("api/v1/dnsServer/records"));

            Assert.That(records.Count(),                               Is.EqualTo(1));

            var status              = await GetJSON(http, "api/v1/dnsServer");

            Assert.That(status["serving"]!.Value<Boolean>(),           Is.True);
            Assert.That(status["recordTypes"]!["A"]!.Value<Int32>(),   Is.EqualTo(1));

            var (refused, why)      = await Send(http, HttpMethod.Post, "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "www.example.org"),
                                                             new JProperty("address",  "not-an-address")));

            Assert.That(refused,                                       Is.EqualTo(HttpStatusCode.BadRequest), why.ToString());

            var (removed, after)    = await Send(http, HttpMethod.Post, "api/v1/dnsServer/records/a/remove",
                                                 new JObject(new JProperty("name",     "www.example.org."),
                                                             new JProperty("address",  "192.0.2.10")));

            Assert.That(removed,                                       Is.EqualTo(HttpStatusCode.OK), after.ToString());
            Assert.That(after["records"]!.Count(),                     Is.Zero);
            Assert.That(((DNSServerNode) Node).Records.Count,          Is.Zero);

            var (nothing, said)     = await Send(http, HttpMethod.Post, "api/v1/dnsServer/records/a/remove",
                                                 new JObject(new JProperty("name",     "www.example.org")));

            Assert.That(nothing,                                       Is.EqualTo(HttpStatusCode.NotFound), said.ToString());

        }

        #endregion

        #region AnOperatorMayReadTheRecordsButNotChangeThem()

        [Test]
        public async Task AnOperatorMayReadTheRecordsButNotChangeThem()
        {

            var password            = "An-Operator-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

            await AccountIn("operator1", DNSServerAccess.Operator.Name, password);

            using var http          = await SignedInAs("operator1", password);

            var records             = JArray.Parse(await http.GetStringAsync("api/v1/dnsServer/records"));

            Assert.That(records.Count(),                               Is.Zero);

            var (status, answer)    = await Send(http, HttpMethod.Post, "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "www.example.org"),
                                                             new JProperty("address",  "192.0.2.10")));

            Assert.That(status,                                        Is.EqualTo(HttpStatusCode.Forbidden), answer.ToString());
            Assert.That(((DNSServerNode) Node).Records.Count,          Is.Zero);

        }

        #endregion

        #region AHostmasterMayChangeTheRecords()

        [Test]
        public async Task AHostmasterMayChangeTheRecords()
        {

            var password            = "A-Hostmaster-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

            await AccountIn("hostmaster1", DNSServerAccess.Hostmaster.Name, password);

            using var http          = await SignedInAs("hostmaster1", password);

            var (status, answer)    = await Send(http, HttpMethod.Post, "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "www.example.org"),
                                                             new JProperty("address",  "192.0.2.10")));

            Assert.That(status,                                        Is.EqualTo(HttpStatusCode.OK), answer.ToString());
            Assert.That(((DNSServerNode) Node).Records.Count,          Is.EqualTo(1));

        }

        #endregion

    }

}
