# DNSServer

One name server for a local network, as a WWCP node: Hermod's DNS server,
answering over UDP and TCP with the records of the `dnsServer` section of the
node's configuration file - and a command line, at the console and over SSH,
that lists, adds and removes them (`dnsServer`, `records`, `aRecords`).

It is not built alone: [DNSServerCLI](https://github.com/OpenChargingCloud/DNSServerCLI)
carries it as a submodule beside the libraries it needs, and runs its tests.
