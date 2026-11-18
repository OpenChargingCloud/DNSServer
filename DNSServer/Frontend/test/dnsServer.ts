/*
 * The stand-in node every kind's page tests share - WWCP_Node's test/node.ts -
 * as this DNS server: a stand-in for fetch, and a document of happy-dom to
 * draw a page into. A DNS server adds nothing of its own to it.
 *
 * Imported first, before a page: lit-html looks for the document as it is
 * loaded, and the pages load it.
 *
 *   import { open, ... } from '../../test/dnsServer.ts';
 *   const { recordsPage } = await import('./records.ts');
 */

import { standIn } from '@node/../test/node.ts';

export * from '@node/../test/node.ts';

standIn({ name: 'DNS Server', icon: 'fa-globe' });
