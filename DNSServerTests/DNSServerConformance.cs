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

using org.GraphDefined.Vanaheimr.Illias;
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

        /// <summary>
        /// The methods the records are changed with: ADD a record, REMOVE one.
        /// </summary>
        private static readonly HttpMethod ADD     = new ("ADD");
        private static readonly HttpMethod REMOVE  = new ("REMOVE");

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
                       HTTPPort:          TestPorts.Free(),
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
        /// An A record added with ADD and removed with REMOVE by whoever may edit
        /// the name server: answered with what was said and the records as they
        /// are now - and what is no address refused, nothing to remove said so,
        /// and a POST, which changed the records before, changing nothing.
        /// </summary>
        [Test]
        public async Task TheRecordsAreChangedOverTheJSONAPI()
        {

            using var http          = await SignedIn();

            var (added, answer)     = await Send(http, ADD,    "api/v1/dnsServer/records/a",
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

            var (refused, why)      = await Send(http, ADD,    "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "www.example.org"),
                                                             new JProperty("address",  "not-an-address")));

            Assert.That(refused,                                       Is.EqualTo(HttpStatusCode.BadRequest), why.ToString());

            var (removed, after)    = await Send(http, REMOVE, "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "www.example.org."),
                                                             new JProperty("address",  "192.0.2.10")));

            Assert.That(removed,                                       Is.EqualTo(HttpStatusCode.OK), after.ToString());
            Assert.That(after["records"]!.Count(),                     Is.Zero);
            Assert.That(((DNSServerNode) Node).Records.Count,          Is.Zero);

            var (nothing, said)     = await Send(http, REMOVE, "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "www.example.org")));

            Assert.That(nothing,                                       Is.EqualTo(HttpStatusCode.NotFound), said.ToString());

            // ADD and REMOVE, not POST: what changes the records is said by its method.
            var (posted, _)         = await Send(http, HttpMethod.Post, "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "www.example.org"),
                                                             new JProperty("address",  "192.0.2.10")));

            Assert.That(posted,                                        Is.Not.EqualTo(HttpStatusCode.OK));
            Assert.That(((DNSServerNode) Node).Records.Count,          Is.Zero);

        }

        #endregion

        #region AAAA_TXT_AndCNAMERecordsAreChangedOverTheJSONAPI()

        /// <summary>
        /// An AAAA, a TXT and a CNAME record added and removed: each with the key
        /// of its type - "address", "text", "target" - and listed with the value
        /// it is removed by; a text taken as it is, quotes and spaces included;
        /// and a record beside a CNAME refused.
        /// </summary>
        [Test]
        public async Task AAAA_TXT_AndCNAMERecordsAreChangedOverTheJSONAPI()
        {

            using var http          = await SignedIn();

            var text                = " v=spf1 \"quoted\" -all";

            var (aaaa, aaaaAnswer)  = await Send(http, ADD,    "api/v1/dnsServer/records/aaaa",
                                                 new JObject(new JProperty("name",     "www.example.org"),
                                                             new JProperty("address",  "2001:db8::10")));

            var (txt,  txtAnswer)   = await Send(http, ADD,    "api/v1/dnsServer/records/txt",
                                                 new JObject(new JProperty("name",     "example.org"),
                                                             new JProperty("text",     text),
                                                             new JProperty("ttl",      300)));

            var (cname, cnameAnswer) = await Send(http, ADD,    "api/v1/dnsServer/records/cname",
                                                 new JObject(new JProperty("name",     "docs.example.org"),
                                                             new JProperty("target",   "www.example.org")));

            Assert.Multiple(() => {
                Assert.That(aaaa,   Is.EqualTo(HttpStatusCode.OK), aaaaAnswer. ToString());
                Assert.That(txt,    Is.EqualTo(HttpStatusCode.OK), txtAnswer.  ToString());
                Assert.That(cname,  Is.EqualTo(HttpStatusCode.OK), cnameAnswer.ToString());
            });

            var records             = JArray.Parse(await http.GetStringAsync("api/v1/dnsServer/records"));
            var byType              = records.ToDictionary(record => record["type"]!.Value<String>()!);

            Assert.Multiple(() => {
                Assert.That(records.Count,                                    Is.EqualTo(3));
                Assert.That(byType["AAAA"] ["value"]!.Value<String>(),        Is.EqualTo("2001:db8::10"));
                Assert.That(byType["TXT"]  ["value"]!.Value<String>(),        Is.EqualTo(text), "the text as it was sent");
                Assert.That(byType["TXT"]  ["data"]! .Value<String>(),        Is.EqualTo("\" v=spf1 \\\"quoted\\\" -all\""), "the text as a zone file writes it");
                Assert.That(byType["TXT"]  ["ttl"]!  .Value<Int64>(),         Is.EqualTo(300));
                Assert.That(byType["CNAME"]["value"]!.Value<String>(),        Is.EqualTo("www.example.org."));
            });

            var (beside, why)       = await Send(http, ADD,    "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "docs.example.org"),
                                                             new JProperty("address",  "192.0.2.10")));

            Assert.That(beside,                                        Is.EqualTo(HttpStatusCode.BadRequest), why.ToString());
            Assert.That(why.ToString(),                                Does.Contain("alias"));

            var (noKey, missing)    = await Send(http, ADD,    "api/v1/dnsServer/records/txt",
                                                 new JObject(new JProperty("name",     "example.org"),
                                                             new JProperty("address",  "192.0.2.10")));

            Assert.That(noKey,                                         Is.EqualTo(HttpStatusCode.BadRequest), missing.ToString());
            Assert.That(missing.ToString(),                            Does.Contain("\\\"text\\\" is missing"));

            foreach (var (type, key, value) in new[] { ("aaaa",  "address", "2001:db8::10"),
                                                       ("txt",   "text",    text),
                                                       ("cname", "target",  "www.example.org.") })
            {

                var name            = byType[type.ToUpperInvariant()]["name"]!.Value<String>()!;

                var (removed, after) = await Send(http, REMOVE, $"api/v1/dnsServer/records/{type}",
                                                  new JObject(new JProperty("name",  name),
                                                              new JProperty(key,     value)));

                Assert.That(removed,                                   Is.EqualTo(HttpStatusCode.OK), after.ToString());

            }

            Assert.That(((DNSServerNode) Node).Records.Count,          Is.Zero);

        }

        #endregion

        #region TheZonesAreChangedOverTheJSONAPI()

        /// <summary>
        /// A zone added with ADD and removed with REMOVE: listed with the SOA and
        /// NS records it makes and what it lacks to be found, its serial counted
        /// on by a record of it, and what cannot be a zone refused.
        /// </summary>
        [Test]
        public async Task TheZonesAreChangedOverTheJSONAPI()
        {

            using var http          = await SignedIn();

            var (added, answer)     = await Send(http, ADD,    "api/v1/dnsServer/zones",
                                                 new JObject(new JProperty("name",         "example.org"),
                                                             new JProperty("nameServers",  new JArray("ns1.example.org", "ns2.example.net"))));

            Assert.That(added,                                         Is.EqualTo(HttpStatusCode.OK), answer.ToString());
            Assert.That(answer["said"]!.Value<String>(),               Does.Contain("authoritative for example.org. from now on"));
            Assert.That(answer["note"]!.Value<String>(),               Does.StartWith("ns1.example.org. has no address yet"));

            var zone                = answer["zones"]![0]!;

            Assert.Multiple(() => {
                Assert.That(answer["zones"]!.Count(),                  Is.EqualTo(1));
                Assert.That(zone["name"]!.Value<String>(),             Is.EqualTo("example.org."));
                Assert.That(zone["nameServers"]!.Values<String>(),     Is.EqualTo(new[] { "ns1.example.org.", "ns2.example.net." }));
                Assert.That(zone["hostmaster"]!.Value<String>(),       Is.EqualTo("hostmaster@example.org"));
                Assert.That(zone["records"]!.Select(record => record["type"]!.Value<String>()), Is.EqualTo(new[] { "SOA", "NS", "NS" }));
                Assert.That(zone["records"]![0]!["data"]!.Value<String>(), Does.StartWith("ns1.example.org. hostmaster.example.org. "));
            });

            var serial              = zone["serial"]!.Value<UInt32>();

            await Send(http, ADD, "api/v1/dnsServer/records/a",
                       new JObject(new JProperty("name",     "ns1.example.org"),
                                   new JProperty("address",  "192.0.2.53")));

            var zones               = JArray.Parse(await http.GetStringAsync("api/v1/dnsServer/zones"));

            Assert.Multiple(() => {
                Assert.That(zones[0]["serial"]!.Value<UInt32>(),       Is.EqualTo(serial + 1));
                Assert.That(zones[0]["note"]!.Type,                    Is.EqualTo(JTokenType.Null), "ns1.example.org. has its address now");
            });

            var status              = await GetJSON(http, "api/v1/dnsServer");

            Assert.That(status["zones"]!.Values<String>(),             Is.EqualTo(new[] { "example.org." }));

            var (again, why)        = await Send(http, ADD,    "api/v1/dnsServer/zones",
                                                 new JObject(new JProperty("name",         "example.org.")));

            var (badList, notNames) = await Send(http, ADD,    "api/v1/dnsServer/zones",
                                                 new JObject(new JProperty("name",         "example.com"),
                                                             new JProperty("nameServers",  "ns.example.com")));

            Assert.Multiple(() => {
                Assert.That(again,                                     Is.EqualTo(HttpStatusCode.BadRequest), why.ToString());
                Assert.That(why.ToString(),                            Does.Contain("already"));
                Assert.That(badList,                                   Is.EqualTo(HttpStatusCode.BadRequest), notNames.ToString());
            });

            var (removed, after)    = await Send(http, REMOVE, "api/v1/dnsServer/zones",
                                                 new JObject(new JProperty("name",         "EXAMPLE.org")));

            Assert.That(removed,                                       Is.EqualTo(HttpStatusCode.OK), after.ToString());
            Assert.That(after["zones"]!.Count(),                       Is.Zero);
            Assert.That(((DNSServerNode) Node).Records.Count,          Is.EqualTo(1), "its records stay");

            var (nothing, said)     = await Send(http, REMOVE, "api/v1/dnsServer/zones",
                                                 new JObject(new JProperty("name",         "example.org")));

            Assert.That(nothing,                                       Is.EqualTo(HttpStatusCode.NotFound), said.ToString());

        }

        #endregion

        #region TheDNSClientsAreChangedOverTheJSONAPI()

        /// <summary>
        /// The DNS clients set with PUT: each key shown with its fingerprint, the
        /// whole of it written down, DNS over SSH switched off and on, and what
        /// cannot be a DNS client refused with nothing changed.
        /// </summary>
        [Test]
        public async Task TheDNSClientsAreChangedOverTheJSONAPI()
        {

            using var http          = await SignedIn();

            var key                 = org.GraphDefined.Vanaheimr.Hermod.SSH.SshPublicKey.FromHostKey(
                                          org.GraphDefined.Vanaheimr.Hermod.SSH.SshHostKey.GenerateEd25519(), "cs001").ToAuthorizedKeyLine();

            var (put, answer)       = await Send(http, HttpMethod.Put, "api/v1/dnsServer/ssh",
                                                 new JObject(new JProperty("clients", new JArray(
                                                     new JObject(new JProperty("name", "cs001"),
                                                                 new JProperty("keys", new JArray(key)))))));

            Assert.That(put,                                           Is.EqualTo(HttpStatusCode.OK), answer.ToString());

            var client              = answer["clients"]![0]!;

            Assert.Multiple(() => {
                Assert.That(answer["enabled"]!.Value<Boolean>(),       Is.True);
                Assert.That(answer["subsystem"]!.Value<String>(),      Is.EqualTo("dns@open.charging.cloud"));
                Assert.That(client["name"]!.Value<String>(),           Is.EqualTo("cs001"));
                Assert.That(client["enabled"]!.Value<Boolean>(),       Is.True);
                Assert.That(client["keys"]![0]!["algorithm"]!.Value<String>(),   Is.EqualTo("ssh-ed25519"));
                Assert.That(client["keys"]![0]!["fingerprint"]!.Value<String>(), Does.StartWith("SHA256:"));
                Assert.That(client["activity"]!["queries"]!.Value<Int64>(),      Is.Zero);
            });

            var (off, switchedOff)  = await Send(http, HttpMethod.Put, "api/v1/dnsServer/ssh",
                                                 new JObject(new JProperty("enabled", false)));

            Assert.That(off,                                           Is.EqualTo(HttpStatusCode.OK), switchedOff.ToString());
            Assert.That(switchedOff["enabled"]!.Value<Boolean>(),      Is.False);
            Assert.That(switchedOff["clients"]!.Count(),               Is.EqualTo(1), "what the request left out stays");

            var settings            = await GetJSON(http, "api/v1/dnsServer/settings");

            Assert.That(settings["ssh"]!["enabled"]!.Value<Boolean>(), Is.False);
            Assert.That(settings["ssh"]!["clients"]![0]!["keys"]![0]!.Value<String>(), Is.EqualTo(key));

            var (twice, why)        = await Send(http, HttpMethod.Put, "api/v1/dnsServer/ssh",
                                                 new JObject(new JProperty("enabled", true),
                                                             new JProperty("clients", new JArray(
                                                                 new JObject(new JProperty("name", "cs002")),
                                                                 new JObject(new JProperty("name", "CS002"))))));

            var (badKey, notAKey)   = await Send(http, HttpMethod.Put, "api/v1/dnsServer/ssh",
                                                 new JObject(new JProperty("clients", new JArray(
                                                     new JObject(new JProperty("name", "cs003"),
                                                                 new JProperty("keys", new JArray("no key at all")))))));

            var (account, isOne)    = await Send(http, HttpMethod.Put, "api/v1/dnsServer/ssh",
                                                 new JObject(new JProperty("clients", new JArray(
                                                     new JObject(new JProperty("name", AdminLogin.ToString()))))));

            Assert.Multiple(() => {
                Assert.That(twice,                                     Is.EqualTo(HttpStatusCode.BadRequest), why.ToString());
                Assert.That(why.ToString(),                            Does.Contain("listed twice"));
                Assert.That(badKey,                                    Is.EqualTo(HttpStatusCode.BadRequest), notAKey.ToString());
                Assert.That(notAKey.ToString(),                        Does.Contain("authorized_keys"));
                Assert.That(account,                                   Is.EqualTo(HttpStatusCode.BadRequest), isOne.ToString());
                Assert.That(isOne.ToString(),                          Does.Contain("is an account"));
            });

            var after               = await GetJSON(http, "api/v1/dnsServer/ssh");

            Assert.Multiple(() => {
                Assert.That(after["enabled"]!.Value<Boolean>(),        Is.False, "a refused request switched DNS over SSH on");
                Assert.That(after["clients"]!.Select(entry => entry["name"]!.Value<String>()), Is.EqualTo(new[] { "cs001" }));
            });

        }

        #endregion

        #region AnOperatorMayReadTheRecordsButNotChangeThem()

        [Test]
        public async Task AnOperatorMayReadTheRecordsButNotChangeThem()
        {

            var password            = "An-Operator-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

            await AccountIn(User_Id.Parse("operator1"), DNSServerAccess.Operator.Name, password);

            using var http          = await SignedInAs("operator1", password);

            var records             = JArray.Parse(await http.GetStringAsync("api/v1/dnsServer/records"));

            Assert.That(records.Count(),                               Is.Zero);

            var (status, answer)    = await Send(http, ADD,    "api/v1/dnsServer/records/a",
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

            await AccountIn(User_Id.Parse("hostmaster1"), DNSServerAccess.Hostmaster.Name, password);

            using var http          = await SignedInAs("hostmaster1", password);

            var (status, answer)    = await Send(http, ADD,    "api/v1/dnsServer/records/a",
                                                 new JObject(new JProperty("name",     "www.example.org"),
                                                             new JProperty("address",  "192.0.2.10")));

            Assert.That(status,                                        Is.EqualTo(HttpStatusCode.OK), answer.ToString());
            Assert.That(((DNSServerNode) Node).Records.Count,          Is.EqualTo(1));

        }

        #endregion

    }

}
