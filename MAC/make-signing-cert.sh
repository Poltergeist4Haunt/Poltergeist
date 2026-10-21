#!/bin/bash
# One-time: creates a local self-signed code-signing certificate named "Poltergeist Local"
# in your login keychain, so rebuilds keep the same identity and macOS keeps its permissions.
set -euo pipefail
NAME="Poltergeist Local"
if security find-certificate -c "$NAME" >/dev/null 2>&1; then echo "Already exists."; exit 0; fi
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
cat > "$T/o.cnf" <<CNF
[req]
distinguished_name=dn
x509_extensions=ext
prompt=no
[dn]
CN=$NAME
[ext]
basicConstraints=critical,CA:false
keyUsage=critical,digitalSignature
extendedKeyUsage=critical,codeSigning
CNF
openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -keyout "$T/k.pem" -out "$T/c.pem" -config "$T/o.cnf" 2>/dev/null
openssl pkcs12 -export -inkey "$T/k.pem" -in "$T/c.pem" -out "$T/p.p12" -passout pass:poltergeist -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 2>/dev/null
security import "$T/p.p12" -k ~/Library/Keychains/login.keychain-db -P poltergeist -T /usr/bin/codesign >/dev/null
echo "Created \"$NAME\"."
