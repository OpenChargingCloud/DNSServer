/**
 * What the DNS server's own pages are held to: what every page of every kind
 * of node is, by the rules of WWCP_Node's test/pages.ts - the records page
 * has a form, the name server and the configuration none.
 *
 * Run with `npm test`. "@node/.." is WWCP_Node/Frontend, where the rules are.
 */

import { everyPageIn } from '@node/../test/pages.ts';


everyPageIn(new URL('./', import.meta.url), {
    withForms: [ 'records.ts' ]
});
