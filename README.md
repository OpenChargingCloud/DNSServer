# DNSServer

![The EV charging test environment, the DNS server (DNS) highlighted](docs/EVChargingTestEnvironment.svg)

One name server for a local network, as a WWCP node: Hermod's DNS server,
answering over UDP and TCP with the records of the `dnsServer` section of the
node's configuration file, authoritatively for the zones it names, with the SOA
and NS records it makes for them - and over SSH, as the subsystem
`dns@open.charging.cloud` of the node's SSH server, to the DNS clients of the
section and the accounts allowed to run `dnsquery`. A web interface, a JSON API
below `/api/v1/dnsServer`, and a command line at the console and over SSH list,
add and remove them (`dnsServer`, `records`, `aRecords`, `aaaaRecords`,
`txtRecords`, `cnameRecords`, `mxRecords`, `srvRecords` and `ptrRecords` for
A, AAAA, TXT, CNAME, MX, SRV and PTR records, `zones`,
and `dnsClients` for the machines that may ask over SSH).

It is not built alone: [DNSServerCLI](https://github.com/OpenChargingCloud/DNSServerCLI)
carries it as a submodule beside the libraries it needs, and runs its tests.
