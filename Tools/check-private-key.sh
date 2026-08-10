#!/usr/bin/env bash
set -u

if [[ "${1:-}" != "--staged" ]]; then
  echo "usage: Tools/check-private-key.sh --staged" >&2
  exit 2
fi

failed=0
begin_marker='-----BEGIN '
private_marker='PRIVATE KEY-----'
end_marker='-----END '

while IFS= read -r -d '' path; do
  lower_path="$(printf '%s' "$path" | tr '[:upper:]' '[:lower:]')"
  case "$lower_path" in
    *.pem|*.key|*.p12|*.pfx)
      echo "private-key check: forbidden key container: $path" >&2
      failed=1
      continue
      ;;
  esac

  blob="$(git show ":$path" 2>/dev/null)" || continue
  if printf '%s\n' "$blob" | grep -Eq -- \
      "${begin_marker}(EC |RSA |OPENSSH |ENCRYPTED )?${private_marker}" && \
    printf '%s\n' "$blob" | grep -Eq '^[A-Za-z0-9+/=]{40,}$' && \
    printf '%s\n' "$blob" | grep -Eq -- \
      "${end_marker}(EC |RSA |OPENSSH |ENCRYPTED )?${private_marker}"; then
    echo "private-key check: private PEM material detected: $path" >&2
    failed=1
  fi
done < <(git diff --cached --name-only --diff-filter=ACMR -z)

if [[ "$failed" -ne 0 ]]; then
  echo "Commit rejected. Keep private keys outside the repository under ~/.myframework-keys/." >&2
fi
exit "$failed"
