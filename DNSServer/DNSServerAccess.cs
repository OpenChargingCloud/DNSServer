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
    /// of the people - and machines - around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two resources, because they are two different things to be trusted
    /// with. The name server itself - its records, its zones and the DNS
    /// clients it lets in over SSH - is what an operator reads and a
    /// hostmaster changes. A query over SSH is what a machine that resolves
    /// its names here runs, and nothing else.
    /// </para>
    /// <para>
    /// The node brings the viewer, who may look at everything, and the
    /// administrators, who may do everything. The configuration file may add
    /// roles and say differently what one of them may do - see the node's
    /// "roles" section.
    /// </para>
    /// </remarks>
    public static class DNSServerAccess
    {

        #region Resources

        /// <summary>
        /// The name server: read, what it listens on, how much it answered, the
        /// records and zones it answers with and who asked over SSH; edited, its
        /// records, its zones and the DNS clients it lets in over SSH.
        /// </summary>
        public const String  NameServer  = "nameserver";

        /// <summary>
        /// DNS over SSH: run, queries over the SSH subsystem
        /// "dns@open.charging.cloud", by an account of this node signed in with
        /// one of its SSH keys.
        /// </summary>
        /// <remarks>
        /// For an account. A machine that is nothing but a DNS client is better
        /// one of the DNS clients of the "dnsServer" section, which the node
        /// confines to the queries - no command line, no SFTP.
        /// </remarks>
        public const String  Query       = "dnsquery";

        /// <summary>
        /// Both.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ NameServer, Query ];

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
        /// An account that is a DNS client and nothing else: it may ask the
        /// name server over SSH, and look at nothing.
        /// </summary>
        public static readonly Role  DNSClient   = new ("dnsclient",
                                                        [ Permission.Run(Query) ],
                                                        "asks the name server over SSH, and nothing else");

        /// <summary>
        /// All three, in the order a sentence naming them reads best.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ Operator, Hostmaster, DNSClient ];

        #endregion

    }

}
