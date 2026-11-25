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

using System.Reflection;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

#endregion

namespace cloud.charging.open.DNSServer.CommandLine
{

    /// <summary>
    /// The command line of a running DNS server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Commands are not listed anywhere. The commands every node has - queryDNS
    /// among them, which asks the name servers this node uses itself - are the
    /// node's, and NodeCLI, which this derives from, finds them in WWCP_Node.
    /// The constructor registers this type as well, so that this assembly is
    /// walked for anything that implements ICLICommand and can be built from a
    /// DNSServerCLI: a command of the name server's own is a new file and
    /// nothing else - 'dnsServer', 'records', 'aRecords', 'aaaaRecords',
    /// 'txtRecords', 'cnameRecords', 'zones' and 'dnsClients' among them.
    /// </para>
    /// <para>
    /// Here, beside the name server, and not in the program that runs it: what
    /// a DNS server can be told is the DNS server's, and is told over SSH as
    /// well as at the console. The node hands it to whoever signs in over SSH
    /// by itself (see DNSServerNode.cs), so that a DNS server run by another
    /// program - the tests, a test environment - has the same commands as one
    /// run by the program DNSServerCLI.
    /// </para>
    /// </remarks>
    public class DNSServerCLI : NodeCLI
    {

        #region Properties

        /// <summary>
        /// The DNS server these commands are about.
        /// </summary>
        public DNSServerNode  DNSServer  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the command line of the given DNS server, at the console.
        /// </summary>
        public DNSServerCLI(DNSServerNode      DNSServer,
                            params Assembly[]  AssembliesWithCLICommands)

            : base(DNSServer, AssembliesWithCLICommands)

        {

            this.DNSServer = DNSServer;

            RegisterCLIType(typeof(DNSServerCLI));

        }

        /// <summary>
        /// Create the command line of the given DNS server on the given terminal,
        /// for the given caller - a session over SSH.
        /// </summary>
        public DNSServerCLI(DNSServerNode      DNSServer,
                            ICLITerminal       Terminal,
                            CLICaller          Caller,
                            params Assembly[]  AssembliesWithCLICommands)

            : base(DNSServer, Terminal, Caller, AssembliesWithCLICommands)

        {

            this.DNSServer = DNSServer;

            RegisterCLIType(typeof(DNSServerCLI));

        }

        #endregion


        #region (protected override) GetPrompt()

        /// <summary>
        /// Which port this DNS server answers its web interface on, because a
        /// machine that is one of several on a bench should say which one it is
        /// before it asks for a command.
        /// </summary>
        protected override String GetPrompt()

            => $"dns:{DNSServer.HTTPPort}> ";

        #endregion

    }

}
