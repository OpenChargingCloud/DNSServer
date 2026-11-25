import { nodeAPI,
         request,
         type Certificate        as NodeCertificate,
         type CertificateImport  as NodeCertificateImport,
         type CertificateStore   as NodeCertificateStore,
         type NodeConfiguration,
         type NodeMe,
         type NodeResource,
         type NodeStatus,
         type Operation }  from '@node/api/client';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module. What follows is what a DNS server says of its
// own: its resource, its configuration's own section, the kinds its store
// keeps - and the name server itself, below "/dnsServer".
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this DNS server: what every node
 * has, and the name server - read, how it stands and its records; edited,
 * its records.
 */
export type Resource = NodeResource | 'nameserver';

/** What somebody signed in to this DNS server may do: an operation on a resource, written "nameserver:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the node is doing right now: what every node says. */
export type Status = NodeStatus;

/**
 * What the DNS server is made of: every node's sections, and its own. Only
 * the shape the Configuration page relies on is named; the fields of each
 * section are drawn from whatever the server sends.
 */
export interface Configuration extends NodeConfiguration {
    dnsServer:  Record<string, unknown>;
}

/** What a certificate in the node's own store is for: the four kinds of TLS. */
export type CertificateKind = 'tlsRoot' | 'clientRoot' | 'tlsServer' | 'tlsIdentity';

/** One certificate in the node's own store. */
export type Certificate = NodeCertificate<CertificateKind>;

/** What an import into the node's own store sends. */
export type CertificateImport = NodeCertificateImport<CertificateKind>;

/** The node's own store, grouped the way it is shown. */
export type CertificateStore = NodeCertificateStore<CertificateKind>;


/** How the name server stands: GET /dnsServer. */
export interface NameServerStatus {
    /** Whether DNS is answered now. */
    serving:           boolean;
    /** "any", or the one address DNS listens on. */
    listen:            string;
    /** Where DNS over UDP listens now - "127.0.0.1:53" - or null while it does not. */
    udp:               string | null;
    /** Where DNS over TCP listens now, or null while it does not. */
    tcp:               string | null;
    /** How many records it answers with. */
    records:           number;
    /** How many of them of each type: { "A": 2 }. */
    recordTypes:       Record<string, number>;
    requestsReceived:  number;
    responsesSent:     number;
}

/** The "dnsServer" section as it is in effect - the switches of this start included. */
export interface NameServerSettings {
    listen:      string;
    port:        number;
    udp:         boolean;
    tcp:         boolean;
    /** The TTL of a record made without one, in seconds. */
    defaultTTL:  number;
    /** The records, as zone-file lines. */
    records:     string[];
}

/** One record the name server answers with. */
export interface ResourceRecord {
    /** Complete, with its trailing dot: "www.example.org.". */
    name:   string;
    /** "A", "AAAA", "MX", ... */
    type:   string;
    class:  string;
    /** In seconds. */
    ttl:    number;
    /** What it answers with, as a zone file writes it: "192.0.2.10", "\"v=spf1 -all\"". */
    data:   string | null;
    /**
     * What it answers with, as it is added and removed - the address, the text
     * unquoted, the name it is an alias of - for A, AAAA, TXT and CNAME
     * records; null for every other type.
     */
    value:  string | null;
    /** The whole record as a zone-file line. */
    line:   string;
}

/** The types of record that are added and removed here; every other type is written into the "dnsServer" section by hand. */
export const changeableTypes = [ 'A', 'AAAA', 'TXT', 'CNAME' ] as const;

export type ChangeableType = typeof changeableTypes[number];

/** Whether records of the given type are added and removed here. */
export function isChangeable(Type: string): Type is ChangeableType {
    return (changeableTypes as readonly string[]).includes(Type);
}

/** The key of what a record answers with: "address" for A and AAAA, "text" for TXT, "target" for CNAME. */
export function keyOf(Type: ChangeableType): 'address' | 'text' | 'target' {
    return Type === 'TXT' ? 'text' : Type === 'CNAME' ? 'target' : 'address';
}

/** A new record: POST /dnsServer/records/{type} - { name, address | text | target, ttl? }. */
export interface NewRecord {
    name:      string;
    address?:  string;
    text?:     string;
    target?:   string;
    /** Seconds, or with s, m, h or d after it - "1h"; left out for the "defaultTTL". */
    ttl?:      string;
}

/** Records taken away: POST /dnsServer/records/{type}/remove - every one of the name and type, where nothing else is given. */
export interface RemovedRecord {
    name:      string;
    address?:  string;
    text?:     string;
    target?:   string;
}

/** What a change of the records answers: what it said, and the records as they are now. */
export interface RecordsChanged {
    said:     string;
    /** What else changed with it - the TTL of the other records of the name and type. */
    note:     string | null;
    records:  ResourceRecord[];
}


/** How the DNS server is asked: the routes every node has, typed with the server's own of them, and the name server. */
export const api = {

    ...nodeAPI<{ me: Me; status: Status; configuration: Configuration; kind: CertificateKind; store: CertificateStore }>(),

    dnsServer: {
        status:    ()                       => request<NameServerStatus>   ('GET',  '/dnsServer'),
        settings:  ()                       => request<NameServerSettings> ('GET',  '/dnsServer/settings'),
        records:   ()                       => request<ResourceRecord[]>   ('GET',  '/dnsServer/records'),
        add:       (type: ChangeableType, record: NewRecord)      => request<RecordsChanged>  ('POST', `/dnsServer/records/${type.toLowerCase()}`, record),
        remove:    (type: ChangeableType, record: RemovedRecord)  => request<RecordsChanged>  ('POST', `/dnsServer/records/${type.toLowerCase()}/remove`, record)
    }

};
