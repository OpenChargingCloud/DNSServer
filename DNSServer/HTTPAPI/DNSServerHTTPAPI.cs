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
    ///   <item>GET  /api/v1/dnsServer/records          - every record: its name, type, class, TTL and data, and its zone-file line.</item>
    ///   <item>POST /api/v1/dnsServer/records/a        - { "name", "address", "ttl"? }: the name answers with the address as well - answered with { "said", "note", "records" }.</item>
    ///   <item>POST /api/v1/dnsServer/records/a/remove - { "name", "address"? }: the name no longer answers with the address, or with any - answered with { "said", "records" }.</item>
    /// </list>
    /// A change is in effect at once and written to the configuration file;
    /// one that could not be written is not in effect either.
    /// </remarks>
    public class DNSServerHTTPAPI : NodeHTTPAPI
    {

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
            AddHandler(root + "/records/a",          PostA,         HTTPMethod.POST);
            AddHandler(root + "/records/a/remove",   PostRemoveA,   HTTPMethod.POST);

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

        #region (private) PostA(Request)

        /// <summary>
        /// One A record more, as the command line's "aRecords add".
        /// </summary>
        private Task<HTTPResponse> PostA(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DNSServerAccess.NameServer), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return Task.FromResult(badRequest);

            if (!TryText(json, "name", true, out var name, out var problem) ||
                !TryText(json, "address", true, out var address, out problem))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, problem));

            // A number of seconds, or the text the command line takes: "1h".
            String? ttl = null;

            if (json["ttl"] is JToken ttlToken && ttlToken.Type != JTokenType.Null)
            {

                if (ttlToken.Type is not (JTokenType.Integer or JTokenType.String))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "\"ttl\" is a number of seconds, or one with s, m, h or d after it."));

                ttl = ttlToken.ToString();

            }

            return Task.FromResult(Answer(Request, DNSServer.AddA(name!, address!, ttl, user.Id.ToString())));

        }

        #endregion

        #region (private) PostRemoveA(Request)

        /// <summary>
        /// One A record, or every one of a name, taken away, as the command
        /// line's "aRecords remove".
        /// </summary>
        private Task<HTTPResponse> PostRemoveA(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DNSServerAccess.NameServer), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return Task.FromResult(badRequest);

            if (!TryText(json, "name",    true,  out var name,    out var problem) ||
                !TryText(json, "address", false, out var address, out problem))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, problem));

            return Task.FromResult(Answer(Request, DNSServer.RemoveA(name!, address, user.Id.ToString())));

        }

        #endregion


        #region (private) Answer(Request, Change)

        /// <summary>
        /// What a change came to: what it said, and the records as they are now
        /// - or why nothing changed, with the status that says whose fault.
        /// </summary>
        private HTTPResponse Answer(HTTPRequest    Request,
                                    RecordsChange  Change)

            => Change.Outcome switch {

                   RecordsChangeOutcome.Changed         => JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                                                               new JProperty("said",     Change.Said),
                                                               new JProperty("note",     Change.Note),
                                                               new JProperty("records",  DNSServer.RecordsJSON())
                                                           )),

                   RecordsChangeOutcome.NothingMatched  => ErrorJSON(Request, HTTPStatusCode.NotFound,            Change.Said),
                   RecordsChangeOutcome.NotSaved        => ErrorJSON(Request, HTTPStatusCode.InternalServerError, $"Nothing was changed, since it could not be saved: {Change.Said}"),
                   _                                    => ErrorJSON(Request, HTTPStatusCode.BadRequest,          Change.Said)

               };

        #endregion

        #region (private static) TryText(JSON, Key, Required, out Text, out Problem)

        private static Boolean TryText(JObject                           JSON,
                                       String                            Key,
                                       Boolean                           Required,
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

            Text = token.Value<String>()!.Trim();
            return true;

        }

        #endregion

    }

}
