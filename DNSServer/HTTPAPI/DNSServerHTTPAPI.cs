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

using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// The JSON API of a DNS server: what every node's has, and the name server
    /// at "/api/v1/dnsServer".
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>GET  /api/v1/dnsServer                  - how the name server stands: where it listens, how many records it answers with, and how much it answered.</item>
    ///   <item>GET  /api/v1/dnsServer/settings         - the "dnsServer" section as it is in effect, the switches of this start included.</item>
    ///   <item>GET  /api/v1/dnsServer/records                 - every record: its name, type, class, TTL, data and value, and its zone-file line.</item>
    ///   <item>ADD    /api/v1/dnsServer/records/{type}        - { "name", "address" | "text" | "target" | "preference" and "exchange" | "priority", "weight", "port" and "target", "ttl"? }: the name answers with the record as well - answered with { "said", "note", "records" }.</item>
    ///   <item>REMOVE /api/v1/dnsServer/records/{type}        - { "name", "address" | "text" | "target" | "exchange"? }: the name no longer answers with the record, or with any of the type - answered with { "said", "records" }.</item>
    ///   <item>GET    /api/v1/dnsServer/zones                 - every zone the name server is authoritative for: its name, name servers, hostmaster, serial and timers, the SOA and NS records it makes, and a "note" on what it lacks.</item>
    ///   <item>ADD    /api/v1/dnsServer/zones                 - { "name", "nameServers"? }: the name server is authoritative for the zone from now on - answered with { "said", "note", "zones" }.</item>
    ///   <item>REMOVE /api/v1/dnsServer/zones                 - { "name" }: it no longer is; its records stay - answered with { "said", "zones" }.</item>
    ///   <item>GET    /api/v1/dnsServer/ssh                   - DNS over SSH, the DNS clients with their keys, and what each login asked over SSH since the start.</item>
    ///   <item>PUT    /api/v1/dnsServer/ssh                   - { "enabled"?, "clients"?: [ { "name", "keys", "enabled" } ] }: DNS over SSH, and who may ask there - answered as GET.</item>
    /// </list>
    /// {type} is a, aaaa, txt, cname, mx, srv or ptr; A and AAAA records carry
    /// an "address", TXT records a "text", taken as it is, a CNAME the "target"
    /// it is an alias of, an MX record its "preference" and "exchange", an SRV
    /// record its "priority", "weight", "port" and "target", and a PTR record
    /// the "target" it points to - its "name" may be an address. An MX record
    /// is removed by its "exchange", an SRV or PTR record by its "target".
    /// A change is in effect at once and written to the configuration file;
    /// one that could not be written is not in effect either.
    /// </remarks>
    public class DNSServerHTTPAPI : NodeHTTPAPI
    {

        #region Data

        /// <summary>
        /// REMOVE a record from a resource: the counterpart of ADD, which Hermod
        /// knows already. Registered once, as Hermod has every method that code
        /// declares itself - a method that arrives over the wire is matched by
        /// its name.
        /// </summary>
        public static readonly HTTPMethod REMOVE = HTTPMethod.Register("REMOVE",
                                                                       IsIdempotent:  true,
                                                                       Description:   "REMOVE a value from a resource");

        #endregion

        #region Properties

        /// <summary>
        /// The DNS server this API speaks for.
        /// </summary>
        public DNSServerNode  DNSServer  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        public DNSServerHTTPAPI(HTTPServer     HTTPServer,
                                DNSServerNode  Node,
                                HTTPExtAPI     ExtAPI,
                                EventLog       Log,
                                HTTPPath?      APIPath   = null,
                                String?        Version   = null)

            : base(HTTPServer,
                   Node,
                   ExtAPI,
                   Log,
                   APIPath,
                   Version ?? typeof(DNSServerHTTPAPI).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")

        {

            this.DNSServer = Node;

            var root = HTTPPath.Root + "v1/dnsServer";

            AddHandler(root,                         GetStatus,     HTTPMethod.GET);
            AddHandler(root + "/settings",           GetSettings,   HTTPMethod.GET);
            AddHandler(root + "/records",            GetRecords,    HTTPMethod.GET);
            AddHandler(root + "/zones",              GetZones,      HTTPMethod.GET);
            AddHandler(root + "/zones",              AddZone,       HTTPMethod.ADD);
            AddHandler(root + "/zones",              RemoveZone,    REMOVE);
            AddHandler(root + "/ssh",                GetSSH,        HTTPMethod.GET);
            AddHandler(root + "/ssh",                PutSSH,        HTTPMethod.PUT);

            // records/a, records/aaaa, records/txt, records/cname: ADD a
            // record, REMOVE one or every one of a name.
            foreach (var type in ResourceRecords.Changeable)
            {

                var path = root + $"/records/{ADNSResourceRecord.TypeName(type).ToLowerInvariant()}";

                AddHandler(path,  request => Add   (type, request),  HTTPMethod.ADD);
                AddHandler(path,  request => Remove(type, request),  REMOVE);

            }

        }

        #endregion


        #region (private) GetStatus(Request)

        private Task<HTTPResponse> GetStatus(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DNSServerAccess.NameServer), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, DNSServer.StatusJSON()));

        }

        #endregion

        #region (private) GetSettings(Request)

        private Task<HTTPResponse> GetSettings(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DNSServerAccess.NameServer), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, DNSServer.Settings.ToJSON()));

        }

        #endregion

        #region (private) GetRecords(Request)

        private Task<HTTPResponse> GetRecords(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DNSServerAccess.NameServer), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, DNSServer.RecordsJSON()));

        }

        #endregion

        #region (private) GetZones(Request)

        private Task<HTTPResponse> GetZones(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DNSServerAccess.NameServer), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, DNSServer.ZonesJSON()));

        }

        #endregion

        #region (private) AddZone(Request)

        /// <summary>
        /// One zone more, as the command line's "zones add".
        /// </summary>
        private Task<HTTPResponse> AddZone(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DNSServerAccess.NameServer), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return Task.FromResult(badRequest);

            if (!TryText(json, "name", true, true, out var name, out var problem))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, problem));

            var nameServers = new List<String>();

            if (json["nameServers"] is JToken nameServersToken && nameServersToken.Type != JTokenType.Null)
            {

                if (nameServersToken is not JArray nameServersArray ||
                    nameServersArray.Any(nameServer => nameServer.Type != JTokenType.String))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "\"nameServers\" is a list of names."));

                nameServers.AddRange(nameServersArray.Select(nameServer => nameServer.Value<String>()!));

            }

            return Task.FromResult(Answer(Request, DNSServer.AddZone(name!, nameServers, user.Id.ToString()), Zones: true));

        }

        #endregion

        #region (private) RemoveZone(Request)

        /// <summary>
        /// One zone less, as the command line's "zones remove".
        /// </summary>
        private Task<HTTPResponse> RemoveZone(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DNSServerAccess.NameServer), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return Task.FromResult(badRequest);

            if (!TryText(json, "name", true, true, out var name, out var problem))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, problem));

            return Task.FromResult(Answer(Request, DNSServer.RemoveZone(name!, user.Id.ToString()), Zones: true));

        }

        #endregion


        #region (private) GetSSH(Request)

        /// <summary>
        /// DNS over SSH and the DNS clients, each key with its fingerprint: what
        /// the DNS clients page shows.
        /// </summary>
        private Task<HTTPResponse> GetSSH(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DNSServerAccess.NameServer), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, DNSServer.SSHJSON()));

        }

        #endregion

        #region (private) PutSSH(Request)

        /// <summary>
        /// DNS over SSH switched on or off, and the DNS clients as a whole: what
        /// the request leaves out stays as it is. Everything is read before
        /// anything is changed.
        /// </summary>
        private Task<HTTPResponse> PutSSH(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DNSServerAccess.NameServer), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return Task.FromResult(badRequest);

            Boolean? enabled = null;

            if (json["enabled"] is JToken enabledToken && enabledToken.Type != JTokenType.Null)
            {

                if (enabledToken.Type != JTokenType.Boolean)
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "\"enabled\" is true or false."));

                enabled = enabledToken.Value<Boolean>();

            }

            List<DNSClientEntry>? clients = null;

            if (json["clients"] is JToken clientsToken && clientsToken.Type != JTokenType.Null)
            {

                if (clientsToken is not JArray clientsArray)
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "\"clients\" is a list of { \"name\", \"keys\", \"enabled\" }."));

                clients = [];

                foreach (var clientToken in clientsArray)
                {

                    if (!DNSServerSettings.TryParseClient(clientToken, out var client, out var error))
                        return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

                    if (clients.Any(known => String.Equals(known.Name, client.Name, StringComparison.OrdinalIgnoreCase)))
                        return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"The DNS client '{client.Name}' is listed twice."));

                    if (!DNSServer.SSHClients.Knows(User_Id.Parse(client.Name)) &&
                        DNSServer.ExtAPI.TryGetUser(User_Id.Parse(client.Name), out _))
                        return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                                         $"'{client.Name}' is an account of this {DNSServer.Kind.Name} already, and a name that is both is let in as neither. Choose another name."));

                    clients.Add(client);

                }

            }

            if (enabled is Boolean on && on != DNSServer.Settings.SSHEnabled)
            {

                if (!DNSServer.TrySetSSHEnabled(on, out var notSaved))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.InternalServerError, notSaved));

                Log.Notice($"'{user.Id}' switched DNS over SSH {(on ? "on" : "off")}.", "dnsserver", "ssh");

            }

            if (clients is not null)
            {

                if (!DNSServer.TrySetSSHClients(clients, out var notSaved))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.InternalServerError, notSaved));

                Log.Notice($"'{user.Id}' set the DNS clients that may ask over SSH: {(clients.Count == 0 ? "none" : String.Join(", ", clients.Select(client => client.Name)))}.",
                           "dnsserver", "ssh", "auth");

            }

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, DNSServer.SSHJSON()));

        }

        #endregion

        #region (static) KeyOf(Type)

        /// <summary>
        /// The key of the JSON object that carries what a record of the given
        /// type is removed by - and, but for MX and SRV, all it answers with:
        /// "address" for A and AAAA, "text" for TXT, "exchange" for MX, and
        /// "target" for CNAME, SRV and PTR.
        /// </summary>
        public static String KeyOf(DNSResourceRecordTypes Type)

            => Type switch {
                   DNSResourceRecordTypes.TXT    => "text",
                   DNSResourceRecordTypes.CNAME  => "target",
                   DNSResourceRecordTypes.MX     => "exchange",
                   DNSResourceRecordTypes.SRV    => "target",
                   DNSResourceRecordTypes.PTR    => "target",
                   _                             => "address"
               };

        /// <summary>
        /// The numbers a record of the given type carries before its name, in
        /// the order a zone file writes them: "preference" of an MX record;
        /// "priority", "weight" and "port" of an SRV record.
        /// </summary>
        public static IReadOnlyList<String> NumbersOf(DNSResourceRecordTypes Type)

            => Type switch {
                   DNSResourceRecordTypes.MX   => [ "preference" ],
                   DNSResourceRecordTypes.SRV  => [ "priority", "weight", "port" ],
                   _                           => []
               };

        #endregion

        #region (private) Add   (Type, Request)

        /// <summary>
        /// One record of the given type more, as the command line's "... add".
        /// </summary>
        private Task<HTTPResponse> Add(DNSResourceRecordTypes  Type,
                                       HTTPRequest             Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DNSServerAccess.NameServer), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return Task.FromResult(badRequest);

            if (!TryText(json, "name",        true, true,                                 out var name, out var problem) ||
                !TryText(json, KeyOf(Type),   true, Type != DNSResourceRecordTypes.TXT,   out var data, out problem))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, problem));

            // An MX or SRV record as a zone file writes it: the numbers, then the name.
            var numbers = new List<String>();

            foreach (var key in NumbersOf(Type))
            {

                if (json[key] is not JToken number || number.Type == JTokenType.Null)
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"\"{key}\" is missing."));

                if (number.Type is not (JTokenType.Integer or JTokenType.String) ||
                    !UInt16.TryParse(number.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"\"{key}\" is a number from 0 to 65535."));

                numbers.Add(number.ToString());

            }

            if (numbers.Count > 0)
                data = String.Join(" ", [ .. numbers, data! ]);

            // A number of seconds, or the text the command line takes: "1h".
            String? ttl = null;

            if (json["ttl"] is JToken ttlToken && ttlToken.Type != JTokenType.Null)
            {

                if (ttlToken.Type is not (JTokenType.Integer or JTokenType.String))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "\"ttl\" is a number of seconds, or one with s, m, h or d after it."));

                ttl = ttlToken.ToString();

            }

            return Task.FromResult(Answer(Request, DNSServer.Add(Type, name!, data!, ttl, user.Id.ToString())));

        }

        #endregion

        #region (private) Remove(Type, Request)

        /// <summary>
        /// One record of the given type, or every one of a name, taken away, as
        /// the command line's "... remove".
        /// </summary>
        private Task<HTTPResponse> Remove(DNSResourceRecordTypes  Type,
                                          HTTPRequest             Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DNSServerAccess.NameServer), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return Task.FromResult(badRequest);

            if (!TryText(json, "name",       true,  true,                                out var name, out var problem) ||
                !TryText(json, KeyOf(Type),  false, Type != DNSResourceRecordTypes.TXT,  out var data, out problem))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, problem));

            return Task.FromResult(Answer(Request, DNSServer.Remove(Type, name!, data, user.Id.ToString())));

        }

        #endregion


        #region (private) Answer(Request, Change, Zones = false)

        /// <summary>
        /// What a change came to: what it said, and the records - or the zones -
        /// as they are now; or why nothing changed, with the status that says
        /// whose fault.
        /// </summary>
        private HTTPResponse Answer(HTTPRequest    Request,
                                    RecordsChange  Change,
                                    Boolean        Zones   = false)

            => Change.Outcome switch {

                   RecordsChangeOutcome.Changed         => JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                                                               new JProperty("said",     Change.Said),
                                                               new JProperty("note",     Change.Note),
                                                               Zones
                                                                   ? new JProperty("zones",    DNSServer.ZonesJSON())
                                                                   : new JProperty("records",  DNSServer.RecordsJSON())
                                                           )),

                   RecordsChangeOutcome.NothingMatched  => ErrorJSON(Request, HTTPStatusCode.NotFound,            Change.Said),
                   RecordsChangeOutcome.NotSaved        => ErrorJSON(Request, HTTPStatusCode.InternalServerError, $"Nothing was changed, since it could not be saved: {Change.Said}"),
                   _                                    => ErrorJSON(Request, HTTPStatusCode.BadRequest,          Change.Said)

               };

        #endregion

        #region (private static) TryText(JSON, Key, Required, Trim, out Text, out Problem)

        /// <summary>
        /// The text of the given key: trimmed, unless it is a text that is
        /// answered with as it is - that of a TXT record.
        /// </summary>
        private static Boolean TryText(JObject                           JSON,
                                       String                            Key,
                                       Boolean                           Required,
                                       Boolean                           Trim,
                                       out String?                       Text,
                                       [NotNullWhen(false)] out String?  Problem)
        {

            Text     = null;
            Problem  = null;

            if (JSON[Key] is not JToken token || token.Type == JTokenType.Null)
            {

                if (!Required)
                    return true;

                Problem = $"\"{Key}\" is missing.";
                return false;

            }

            if (token.Type != JTokenType.String || String.IsNullOrWhiteSpace(token.Value<String>()))
            {
                Problem = $"\"{Key}\" is text, and not empty.";
                return false;
            }

            Text = Trim
                       ? token.Value<String>()!.Trim()
                       : token.Value<String>()!;

            return true;

        }

        #endregion

    }

}
