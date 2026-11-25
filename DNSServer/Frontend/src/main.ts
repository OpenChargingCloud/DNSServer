import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { html } from '@node/view';
import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';
import { nameServerPage } from './pages/nameServer';
import { recordsPage } from './pages/records';
import { zonesPage } from './pages/zones';

// What a DNS server has pages for of its own is the name server - where it
// listens and what it answered - at "/dnsServer", first in the menu, and
// below it the records it answers with and the zones it is authoritative for. Everything else is what every node
// has: its configuration, the name servers it asks itself, the time servers it
// checks its clock against, the SSH server, the node's own certificate store
// and the log - see WWCP_Node's start.ts.
startNode({

    name:  'DNS Server',
    icon:  'fa-globe',

    menu: [
        { path: '/dnsServer', label: 'Name server', icon: 'fa-globe', permission: [ 'nameserver:read' ], children: [
            { path: '/dnsServer/records', label: 'Records', icon: 'fa-list',    permission: [ 'nameserver:read' ] },
            { path: '/dnsServer/zones',   label: 'Zones',   icon: 'fa-sitemap', permission: [ 'nameserver:read' ] }
        ] },
        nodeMenu.configuration([
            // The name servers it asks itself - not the one it is.
            nodeMenu.dns,
            nodeMenu.nts,
            nodeMenu.ssh,
            { ...nodeMenu.certificates, label: 'Trusted certificates' },
            nodeMenu.identities
        ]),
        nodeMenu.logs
    ],

    // The node's own store is what this server believes: the roots of the
    // name and time servers it asks itself.
    certificates: {
        title:  'Trusted certificates',
        hints: {
            believes:  html`
                What this DNS server believes, beside the roots of this machine: a name server or a time server it
                asks itself, with a CA of its own - or a self-signed certificate - is reached through one of these.
            `,
            presents:  html`
                What it will show a server that asks who it is, with its private key. Kept for when this DNS server
                connects to one that does; nothing here presents it yet.
            `
        }
    },

    // "/" is every node's: the first page of the menu the person signed in may
    // open - the name server for whoever may read it.
    pages: {

        '/configuration':      configurationPage,
        '/dnsServer':          nameServerPage,
        '/dnsServer/records':  recordsPage,
        '/dnsServer/zones':    zonesPage

    }

});
