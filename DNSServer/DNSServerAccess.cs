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

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// What a DNS server adds to the resources every node has, and the roles
    /// of the people around it.
    /// </summary>
    /// <remarks>
    /// The node brings the viewer, who may look at everything, and the
    /// administrators, who may do everything. The configuration file may add
    /// roles and say differently what one of them may do - see the node's
    /// "roles" section.
    /// </remarks>
    public static class DNSServerAccess
    {

        #region Resources

        /// <summary>
        /// The name server: read, what it listens on, how much it answered and
        /// the records it answers with; edited, its records.
        /// </summary>
        public const String  NameServer  = "nameserver";

        /// <summary>
        /// The one resource of a DNS server's own.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ NameServer ];

        #endregion

        #region Roles

        /// <summary>
        /// Whoever runs this name server day to day: may look at everything
        /// and ask whether the network works - but may not change what the
        /// server answers.
        /// </summary>
        public static readonly Role  Operator    = new ("operator",
                                                        [ Permission.Read(Permission.AnyResource),
                                                          Permission.Run (NodeResources.DNS),
                                                          Permission.Run (NodeResources.NTS) ],
                                                        "runs the DNS server day to day: looks at everything, and asks whether the network works");

        /// <summary>
        /// Whoever keeps the records: may look at everything, and change the
        /// records the name server answers with.
        /// </summary>
        public static readonly Role  Hostmaster  = new ("hostmaster",
                                                        [ Permission.Read(Permission.AnyResource),
                                                          Permission.Edit(NameServer),
                                                          Permission.Run (NodeResources.DNS) ],
                                                        "keeps the records of the DNS server: looks at everything, and adds and removes records");

        /// <summary>
        /// Both, in the order a sentence naming them reads best.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ Operator, Hostmaster ];

        #endregion

    }

}
