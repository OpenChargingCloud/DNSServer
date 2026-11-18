/**
 * The records page drawn, in a document of happy-dom, against a stand-in DNS
 * server that keeps what it is sent: an A record added - with a TTL and
 * without - one removed after asking, what the server refused said beside the
 * form with what was typed kept, records of other types listed without a way
 * to remove them, and for whoever may only look, nothing that changes
 * anything.
 */

import { asked, field, open, refused, said, submit, type, until, type Asked } from '../../test/dnsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { recordsPage } = await import('./records.ts');


interface Kept { name: string; type: string; ttl: number; data: string }

let records: Kept[] = [];

function asRecord(Record: Kept): Record<string, unknown> {
    return { ...Record, class: 'IN', line: `${Record.name} ${Record.ttl} IN ${Record.type} ${Record.data}` };
}

function dnsServer({ method, path, body }: Asked): unknown {

    if (method === 'GET' && path === '/dnsServer/records')
        return records.map(asRecord);

    if (method === 'POST' && path === '/dnsServer/records/a') {

        const { name, address, ttl } = body as { name: string; address: string; ttl?: string };

        if (!/^\d+\.\d+\.\d+\.\d+$/.test(address))
            return refused(400, `'${address}' is no IPv4 address.`);

        const full = name.endsWith('.') ? name : `${name}.`;

        records = [ ...records, { name: full, type: 'A', ttl: ttl === undefined ? 3600 : 600, data: address } ];

        return { said: `${full} answers with ${address} from now on.`, note: null, records: records.map(asRecord) };

    }

    if (method === 'POST' && path === '/dnsServer/records/a/remove') {

        const { name, address } = body as { name: string; address?: string };

        records = records.filter(record => !(record.type === 'A' && record.name === name && (address === undefined || record.data === address)));

        return { said: `${name} no longer answers with ${address}.`, note: null, records: records.map(asRecord) };

    }

    return undefined;

}

function posts(): { path: string; body: unknown }[] {
    return asked.filter(one => one.method === 'POST').map(one => ({ path: one.path, body: one.body }));
}


describe('the records', () => {

    it('adds an A record - the TTL left out where none was typed - and says so', async () => {

        records = [];

        const root = await open(recordsPage, '/dnsServer/records', [ 'nameserver:read', 'nameserver:edit' ],
                                dnsServer, root => root.querySelector('#no-records') !== null);

        type(field(root, '#add-a', 'name'),    'www.example.org');
        type(field(root, '#add-a', 'address'), '192.0.2.10');
        submit(root, '#add-a');

        await until(() => root.querySelector('tr[data-record^="www.example.org."]') !== null, 'the record was not drawn');

        assert.deepEqual(posts(), [ { path: '/dnsServer/records/a', body: { name: 'www.example.org', address: '192.0.2.10' } } ]);
        assert.match(root.querySelector('#add-a-note')!.textContent!, /www\.example\.org\. answers with 192\.0\.2\.10 from now on\. Saved, and in effect\./);
        assert.equal(field(root, '#add-a', 'name').value, '', 'the form kept what was sent');
        assert.match(root.querySelector('tr[data-record^="www.example.org."]')!.textContent!, /A\s+3600 s\s+192\.0\.2\.10/);

    });

    it('sends the TTL as it was typed', async () => {

        records = [];

        const root = await open(recordsPage, '/dnsServer/records', [ 'nameserver:read', 'nameserver:edit' ],
                                dnsServer, root => root.querySelector('#add-a') !== null);

        type(field(root, '#add-a', 'name'),    'api.example.org.');
        type(field(root, '#add-a', 'address'), '192.0.2.20');
        type(field(root, '#add-a', 'ttl'),     '10m');
        submit(root, '#add-a');

        await until(() => posts().length === 1, 'nothing was sent');

        assert.deepEqual(posts()[0]!.body, { name: 'api.example.org.', address: '192.0.2.20', ttl: '10m' });

    });

    it('says what the server refused, and keeps what was typed', async () => {

        records = [];

        const root = await open(recordsPage, '/dnsServer/records', [ 'nameserver:read', 'nameserver:edit' ],
                                dnsServer, root => root.querySelector('#add-a') !== null);

        type(field(root, '#add-a', 'name'),    'www.example.org');
        type(field(root, '#add-a', 'address'), 'not-an-address');
        submit(root, '#add-a');

        await until(() => root.querySelector('#add-a-error')!.textContent !== '', 'the refusal was not said');

        assert.match(root.querySelector('#add-a-error')!.textContent!, /no IPv4 address/);
        assert.equal(field(root, '#add-a', 'address').value, 'not-an-address');

    });

    it('removes an A record after asking, and leaves the others', async () => {

        records = [ { name: 'host.example.org.', type: 'A',    ttl: 300,  data: '192.0.2.1' },
                    { name: 'host.example.org.', type: 'A',    ttl: 300,  data: '192.0.2.2' },
                    { name: 'mail.example.org.', type: 'AAAA', ttl: 3600, data: '2001:db8::25' } ];

        const root = await open(recordsPage, '/dnsServer/records', [ 'nameserver:read', 'nameserver:edit' ],
                                dnsServer, root => root.querySelectorAll('tr[data-record]').length === 3);

        assert.equal(root.querySelector('tr[data-record^="mail.example.org."] button'), null, 'a record of another type can be removed here');

        root.querySelector<HTMLButtonElement>('button[data-remove="host.example.org. 192.0.2.1"]')!.click();

        await until(() => root.querySelectorAll('tr[data-record]').length === 2, 'the record was not removed');

        assert.ok(said.some(text => /Remove host\.example\.org\. 192\.0\.2\.1\?/.test(text)), 'nobody was asked before it went');
        assert.deepEqual(posts(), [ { path: '/dnsServer/records/a/remove', body: { name: 'host.example.org.', address: '192.0.2.1' } } ]);
        assert.match(root.querySelector('#records-note')!.textContent!, /no longer answers with 192\.0\.2\.1/);

    });

    it('changes nothing for whoever may only look, and says why', async () => {

        records = [ { name: 'host.example.org.', type: 'A', ttl: 300, data: '192.0.2.1' } ];

        const root = await open(recordsPage, '/dnsServer/records', [ 'nameserver:read' ],
                                dnsServer, root => root.querySelector('tr[data-record]') !== null);

        assert.match(root.querySelector('.notice')!.textContent!, /may look at the records but not change them/);

        for (const button of root.querySelectorAll<HTMLButtonElement>('button[data-remove], #add-a button[type="submit"]'))
            assert.equal(button.disabled, true, `${button.outerHTML} can be pressed`);

    });

});
