/**
 * The name server page drawn, in a document of happy-dom, against a stand-in
 * DNS server: where it answers, the line to ask it with, its records by type
 * and how much it answered - and that it says so where it does not answer.
 */

import { open, until, type Asked } from '../../test/dnsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { nameServerPage, ttlWords } = await import('./nameServer.ts');


let status: Record<string, unknown> = {};

const settings = { listen: '127.0.0.1', port: 5353, udp: true, tcp: true, defaultTTL: 3600, records: [], zones: [] };

function dnsServer({ path }: Asked): unknown {

    if (path === '/dnsServer')
        return status;

    if (path === '/dnsServer/settings')
        return settings;

    return undefined;

}


describe('the name server', () => {

    it('says where it answers, how to ask it, its records and zones and how much it answered', async () => {

        status = { serving: true, listen: '127.0.0.1', udp: '127.0.0.1:5353', tcp: '127.0.0.1:5353',
                   records: 3, recordTypes: { A: 2, AAAA: 1 }, zones: [ 'example.org.', 'lab.example.org.' ],
                   requestsReceived: 12, responsesSent: 11 };

        const root = await open(nameServerPage, '/dnsServer', [ 'nameserver:read' ],
                                dnsServer, root => root.querySelector('#listeners') !== null);

        assert.match(root.querySelector('#serving')!.textContent!,      /answering/);
        assert.equal(root.querySelector('#udp')!.textContent,           '127.0.0.1:5353');
        assert.equal(root.querySelector('#ask-with')!.textContent,      'dig @127.0.0.1 -p 5353 www.example.org A');
        assert.match(root.querySelector('#record-count')!.textContent!, /3 records: 2 A, 1 AAAA\./);
        assert.equal(root.querySelector('#default-ttl')!.textContent,   '3600 s (1 h)');
        assert.equal(root.querySelector('#zone-names')!.textContent,    'example.org., lab.example.org.');
        assert.equal(root.querySelector('#requests')!.textContent,      '12');
        assert.equal(root.querySelector('#responses')!.textContent,     '11');

    });

    it('says so where it does not answer', async () => {

        status = { serving: false, listen: 'any', udp: null, tcp: null,
                   records: 0, recordTypes: {}, zones: [], requestsReceived: 0, responsesSent: 0 };

        const root = await open(nameServerPage, '/dnsServer', [ 'nameserver:read' ],
                                dnsServer, root => root.querySelector('#listeners') !== null);

        await until(() => /not answering/.test(root.querySelector('#serving')!.textContent!), 'it was not said');

        assert.equal(root.querySelector('#ask-with'), null, 'a line to ask a server that does not listen');
        assert.equal(root.querySelector('#udp')!.textContent, 'not listening');
        assert.match(root.querySelector('#record-count')!.textContent!, /No record yet/);
        assert.match(root.querySelector('#zone-names')!.textContent!,   /^none/);

    });

    it('says a TTL in the unit it is a whole number of', () => {

        assert.equal(ttlWords(86400), '86400 s (1 d)');
        assert.equal(ttlWords(600),   '600 s (10 min)');
        assert.equal(ttlWords(90),    '90 s');

    });

});
