#!/bin/sh
# Operator deploy hook. No key is printed or included in an image.
set -eu
if [ "$#" -ne 3 ]; then
  echo 'Usage: install-edge-certificate.sh CERTBOT_LINEAGE TLS_CERTIFICATE_DIR COMPOSE_ENV_FILE' >&2
  exit 2
fi
lineage=$1
target=$2
compose_env=$3
for absolute in "$lineage" "$target" "$compose_env"; do
  case "$absolute" in /*) ;; *) echo 'All paths must be absolute.' >&2; exit 2;; esac
done
[ "$target" != / ] || { echo 'Certificate target cannot be the filesystem root.' >&2; exit 2; }
if [ -e "$target/current" ] && [ ! -L "$target/current" ]; then
  echo 'Certificate current must be a version symlink, not an existing directory/file.' >&2
  exit 2
fi
[ -f "$lineage/fullchain.pem" ] && [ -f "$lineage/privkey.pem" ] && [ -f "$compose_env" ]
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
project_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
openssl x509 -in "$lineage/fullchain.pem" -noout -checkend 86400 >/dev/null
openssl x509 -in "$lineage/fullchain.pem" -noout -checkhost svault.ir >/dev/null
openssl x509 -in "$lineage/fullchain.pem" -noout -checkhost app.svault.ir >/dev/null
openssl x509 -in "$lineage/fullchain.pem" -noout -checkhost api.svault.ir >/dev/null
# Compare public keys, never private-key output.
cert_public=$(openssl x509 -in "$lineage/fullchain.pem" -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256)
key_public=$(openssl pkey -in "$lineage/privkey.pem" -pubout -outform DER | openssl dgst -sha256)
[ "$cert_public" = "$key_public" ] || { echo 'Certificate and key do not match.' >&2; exit 1; }
install -d -m 0750 -g 101 "$target"
version=$(mktemp -d "$target/version.XXXXXXXX")
chgrp 101 "$version"
chmod 0750 "$version"
install -m 0644 -g 101 "$lineage/fullchain.pem" "$version/fullchain.pem"
install -m 0640 -g 101 "$lineage/privkey.pem" "$version/privkey.pem"
previous=$(readlink "$target/current" || true)
next_link="$target/.current-$$"
ln -s "$(basename "$version")" "$next_link"
mv -Tf "$next_link" "$target/current"
compose() { docker compose --project-directory "$project_dir" --env-file "$compose_env" -f "$project_dir/deploy/compose.yml" "$@"; }
if compose exec -T edge nginx -t && compose exec -T edge nginx -s reload; then
  echo 'Certificate installed and edge configuration reloaded.'
else
  if [ -n "$previous" ]; then
    ln -s "$previous" "$next_link"
    mv -Tf "$next_link" "$target/current"
  fi
  echo 'Edge validation/reload failed. Previous certificate link restored when available; existing workers were not stopped.' >&2
  exit 1
fi
