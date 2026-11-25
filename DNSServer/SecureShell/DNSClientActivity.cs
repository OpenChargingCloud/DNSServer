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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

#endregion

namespace cloud.charging.open.DNSServer.SecureShell
{

    /// <summary>
    /// What one login - a DNS client, or an account allowed to run "dnsquery" -
    /// asked over SSH since this program started: how often, when last, from
    /// where and what.
    /// </summary>
    /// <remarks>
    /// Kept in memory only - what lasts is in the log. A login turned away is
    /// said there by the SSH server, and never reaches the name server.
    /// </remarks>
    /// <param name="Queries">How many queries it sent.</param>
    /// <param name="LastAt">When it sent the last, or null.</param>
    /// <param name="LastFrom">Where the last came from, or null.</param>
    /// <param name="LastQuestion">What the last asked: "www.example.org. A", or null.</param>
    public sealed record DNSClientActivity(Int64            Queries,
                                           DateTimeOffset?  LastAt,
                                           String?          LastFrom,
                                           String?          LastQuestion)
    {

        /// <summary>
        /// Nothing yet.
        /// </summary>
        public static readonly DNSClientActivity  None = new (0, null, null, null);


        /// <summary>
        /// This and the given query.
        /// </summary>
        /// <param name="At">When it arrived.</param>
        /// <param name="Request">The query.</param>
        public DNSClientActivity With(DateTimeOffset  At,
                                      DNSPacket       Request)

            => new (Queries + 1,
                    At,
                    Request.RemoteSocket == IPSocket.Zero ? null : Request.RemoteSocket.ToString(),
                    QuestionOf(Request));


        /// <summary>
        /// The first question of a query, as a zone file writes it: "www.example.org. A".
        /// </summary>
        public static String? QuestionOf(DNSPacket Request)

            => Request.Questions.FirstOrDefault() is DNSQuestion question
                   ? $"{question.DomainName.FullName} {ADNSResourceRecord.TypeName(question.QueryType)}"
                   : null;


        /// <summary>
        /// As the status of the JSON API says it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("queries",       Queries),
                   new JProperty("lastAt",        LastAt?.ToString("o")),
                   new JProperty("lastFrom",      LastFrom),
                   new JProperty("lastQuestion",  LastQuestion)
               );

    }

}
