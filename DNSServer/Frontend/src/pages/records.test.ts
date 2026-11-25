/**
 * The records page drawn, in a document of happy-dom, against a stand-in DNS
 * server that keeps what it is sent: an A record added - with a TTL and
 * without - a TXT record with its text as it was typed, a CNAME with the name
 * it is an alias of, what was typed kept when another type is picked, records
 * removed after asking, what the server refused said beside the form with
 * what was typed kept, records of other types listed without a way to remove
 * them, and for whoever may only look, nothing that changes anything.
 */

import { asked, change, field, open, refused, said, submit, type, until, type Asked } from '../../test/dnsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { recordsPage } = await import('./records.ts');


interface Kept { name: string; type: string; ttl: number; value: string }

let records: Kept[] = [];

/** What a zone file writes for a record: a text in quotes, everything else as it is. */
function dataOf(Record: Kept): string {
    return Record.type === 'TXT' ? `"${Record.value.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"` : Record.value;
}

function asRecord(Record: Kept): Record<string, unknown> {
    return {
        name:   Record.name,
        type:   Record.type,
        class:  'IN',
        ttl:    Record.ttl,
        data:   dataOf(Record),
        value:  [ 'A', 'AAAA', 'TXT', 'CNAME' ].includes(Record.type) ? Record.value : null,
        line:   `${Record.name} ${Record.ttl} IN ${Record.type} ${dataOf(Record)}`
    };
}

const keys: Record<string, string> = { a: 'address', aaaa: 'address', txt: 'text', cname: 'target' };

function dnsServer({ method, path, body }: Asked): unknown {

    if (method === 'GET' && path === '/dnsServer/records')
        return records.map(asRecord);

    const added = /^\/dnsServer\/records\/(a|aaaa|txt|cname)$/.exec(path);

    if (method === 'POST' && added !== null) {

        const kind  = added[1]!;
        const sent  = body as Record<string, string>;
        const value = sent[keys[kind]!]!;

        if (kind === 'a' && !/^\d+\.\d+\.\d+\.\d+$/.test(value))
            return refused(400, `'${value}' is no IPv4 address.`);

        const full = sent.name!.endsWith('.') ? sent.name! : `${sent.name}.`;

        records = [ ...records, { name: full, type: kind.toUpperCase(), ttl: sent.ttl === undefined ? 3600 : 600, value } ];

        return { said: `${full} answers with ${value} from now on.`, note: null, records: records.map(asRecord) };

    }

    const removed = /^\/dnsServer\/records\/(a|aaaa|txt|cname)\/remove$/.exec(path);

    if (method === 'POST' && removed !== null) {

        const kind  = removed[1]!.toUpperCase();
        const sent  = body as Record<string, string>;
        const value = sent[keys[removed[1]!]!];

        records = records.filter(record => !(record.type === kind && record.name === sent.name && (value === undefined || record.value === value)));

        return { said: `${sent.name} no longer answers with ${value}.`, note: null, records: records.map(asRecord) };

    }

    return undefined;

}

function posts(): { path: string; body: unknown }[] {
    return asked.filter(one => one.method === 'POST').map(one => ({ path: one.path, body: one.body }));
}

const mayEdit = [ 'nameserver:read', 'nameserver:edit' ];


describe('the records', () => {

    it('adds an A record - the TTL left out where none was typed - and says so', async () => {

        records = [];

        const root = await open(recordsPage, '/dnsServer/records', mayEdit,
                                dnsServer, root => root.querySelector('#no-records') !== null);

        type(field(root, '#add-record', 'name'), 'www.example.org');
        type(field(root, '#add-record', 'data'), '192.0.2.10');
        submit(root, '#add-record');

        await until(() => root.querySelector('tr[data-record^="www.example.org."]') !== null, 'the record was not drawn');

        assert.deepEqual(posts(), [ { path: '/dnsServer/records/a', body: { name: 'www.example.org', address: '192.0.2.10' } } ]);
        assert.match(root.querySelector('#add-record-note')!.textContent!, /www\.example\.org\. answers with 192\.0\.2\.10 from now on\. Saved, and in effect\./);
        assert.equal(field(root, '#add-record', 'name').value, '', 'the form kept what was sent');
        assert.match(root.querySelector('tr[data-record^="www.example.org."]')!.textContent!, /A\s+3600 s\s+192\.0\.2\.10/);

    });

    it('sends the TTL as it was typed', async () => {

        records = [];

        const root = await open(recordsPage, '/dnsServer/records', mayEdit,
                                dnsServer, root => root.querySelector('#add-record') !== null);

        type(field(root, '#add-record', 'name'), 'api.example.org.');
        type(field(root, '#add-record', 'data'), '192.0.2.20');
        type(field(root, '#add-record', 'ttl'),  '10m');
        submit(root, '#add-record');

        await until(() => posts().length === 1, 'nothing was sent');

        assert.deepEqual(posts()[0]!.body, { name: 'api.example.org.', address: '192.0.2.20', ttl: '10m' });

    });

    it('asks for what another type answers with, and keeps what was typed', async () => {

        records = [];

        const root = await open(recordsPage, '/dnsServer/records', mayEdit,
                                dnsServer, root => root.querySelector('#add-record') !== null);

        type(field(root, '#add-record', 'name'), 'example.org');
        change(field<HTMLSelectElement>(root, '#add-record', 'type'), 'TXT');

        await until(() => root.querySelector('#add-record-data-label')!.textContent === 'Text', 'the form does not ask for a text');

        assert.equal(field(root, '#add-record', 'name').value, 'example.org', 'what was typed went with the type');
        assert.equal(field(root, '#add-record', 'data').placeholder, 'v=spf1 -all');

        // A text is sent as it was typed: spaces, quotes and all.
        type(field(root, '#add-record', 'data'), ' v=spf1 "quoted" -all ');
        submit(root, '#add-record');

        await until(() => posts().length === 1, 'nothing was sent');

        assert.deepEqual(posts()[0], { path: '/dnsServer/records/txt', body: { name: 'example.org', text: ' v=spf1 "quoted" -all ' } });

        await until(() => root.querySelector('tr[data-record^="example.org."]') !== null, 'the record was not drawn');

        assert.equal(field<HTMLSelectElement>(root, '#add-record', 'type').value, 'TXT', 'the type picked is kept for the next record');

    });

    it('adds a CNAME with the name it is an alias of', async () => {

        records = [];

        const root = await open(recordsPage, '/dnsServer/records', mayEdit,
                                dnsServer, root => root.querySelector('#add-record') !== null);

        change(field<HTMLSelectElement>(root, '#add-record', 'type'), 'CNAME');

        await until(() => root.querySelector('#add-record-data-label')!.textContent === 'Alias of', 'the form does not ask for a target');

        type(field(root, '#add-record', 'name'), 'docs.example.org');
        type(field(root, '#add-record', 'data'), ' www.example.org ');
        submit(root, '#add-record');

        await until(() => posts().length === 1, 'nothing was sent');

        assert.deepEqual(posts()[0], { path: '/dnsServer/records/cname', body: { name: 'docs.example.org', target: 'www.example.org' } });

    });

    it('says what the server refused, and keeps what was typed', async () => {

        records = [];

        const root = await open(recordsPage, '/dnsServer/records', mayEdit,
                                dnsServer, root => root.querySelector('#add-record') !== null);

        type(field(root, '#add-record', 'name'), 'www.example.org');
        type(field(root, '#add-record', 'data'), 'not-an-address');
        submit(root, '#add-record');

        await until(() => root.querySelector('#add-record-error')!.textContent !== '', 'the refusal was not said');

        assert.match(root.querySelector('#add-record-error')!.textContent!, /no IPv4 address/);
        assert.equal(field(root, '#add-record', 'data').value, 'not-an-address');

    });

    it('removes an A record after asking, and leaves the others', async () => {

        records = [ { name: 'host.example.org.', type: 'A',    ttl: 300,  value: '192.0.2.1' },
                    { name: 'host.example.org.', type: 'A',    ttl: 300,  value: '192.0.2.2' },
                    { name: 'mail.example.org.', type: 'AAAA', ttl: 3600, value: '2001:db8::25' },
                    { name: 'example.org.',      type: 'MX',   ttl: 3600, value: '10 mail.example.org.' } ];

        const root = await open(recordsPage, '/dnsServer/records', mayEdit,
                                dnsServer, root => root.querySelectorAll('tr[data-record]').length === 4);

        assert.notEqual(root.querySelector('tr[data-record^="mail.example.org."] button'), null, 'an AAAA record cannot be removed here');
        assert.equal   (root.querySelector('tr[data-record^="example.org."] button'),      null, 'an MX record can be removed here');

        root.querySelector<HTMLButtonElement>('button[data-remove="host.example.org. A 192.0.2.1"]')!.click();

        await until(() => root.querySelectorAll('tr[data-record]').length === 3, 'the record was not removed');

        assert.ok(said.some(text => /Remove host\.example\.org\. A 192\.0\.2\.1\?/.test(text)), 'nobody was asked before it went');
        assert.deepEqual(posts(), [ { path: '/dnsServer/records/a/remove', body: { name: 'host.example.org.', address: '192.0.2.1' } } ]);
        assert.match(root.querySelector('#records-note')!.textContent!, /no longer answers with 192\.0\.2\.1/);

    });

    it('removes a TXT record by its text, not by how a zone file quotes it', async () => {

        records = [ { name: 'example.org.', type: 'TXT', ttl: 300, value: 'v=spf1 "quoted" -all' },
                    { name: 'example.org.', type: 'TXT', ttl: 300, value: 'other' } ];

        const root = await open(recordsPage, '/dnsServer/records', mayEdit,
                                dnsServer, root => root.querySelectorAll('tr[data-record]').length === 2);

        assert.match(root.querySelector('tr[data-record] .record-data')!.textContent!, /^"v=spf1 \\"quoted\\" -all"$/);

        root.querySelector<HTMLButtonElement>('tr[data-record] button[data-remove]')!.click();

        await until(() => root.querySelectorAll('tr[data-record]').length === 1, 'the record was not removed');

        assert.deepEqual(posts(), [ { path: '/dnsServer/records/txt/remove', body: { name: 'example.org.', text: 'v=spf1 "quoted" -all' } } ]);

    });

    it('changes nothing for whoever may only look, and says why', async () => {

        records = [ { name: 'host.example.org.', type: 'A', ttl: 300, value: '192.0.2.1' } ];

        const root = await open(recordsPage, '/dnsServer/records', [ 'nameserver:read' ],
                                dnsServer, root => root.querySelector('tr[data-record]') !== null);

        assert.match(root.querySelector('.notice')!.textContent!, /may look at the records but not change them/);

        for (const control of root.querySelectorAll<HTMLButtonElement | HTMLSelectElement>('button[data-remove], #add-record button[type="submit"], #add-record select'))
            assert.equal(control.disabled, true, `${control.outerHTML} can be used`);

    });

});
