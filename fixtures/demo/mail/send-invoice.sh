#!/usr/bin/env bash
# Mails a demo PDF to a GreenMail mailbox as Nordwind's mail server would send it, with the Authentication-Results
# header the receiving server writes (GreenMail writes none), so the DMARC-checked sender rules of docs/11 §7 apply.
# It names the receiver mx.<recipient domain>, which compose lists in Mail:TrustedAuthServIds (no other is believed).
# docs/09 "Demo: routing and layouts".
#
#   fixtures/demo/mail/send-invoice.sh <pdf> <to> [subject] [dmarc]
#
#   dmarc    pass (default), fail, or none (no Authentication-Results header at all)
#   FROM     the sender (default rechnungsversand@nordwind.example)
#   SMTP_URL GreenMail's SMTP port (default smtp://localhost:3025, the compose mapping)
#   EML_OUT  write the message to this file instead of sending it
set -euo pipefail

if [[ $# -lt 2 ]]; then
  sed -n '7,12p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
fi
pdf=$1
to=$2
subject=${3:-"Rechnung $(basename "$pdf" .pdf)"}
dmarc=${4:-pass}
from=${FROM:-rechnungsversand@nordwind.example}
smtp=${SMTP_URL:-smtp://localhost:3025}
domain=${from#*@}
name=$(basename "$pdf")
boundary="nw-$(date +%s)-$RANDOM"

eml=${EML_OUT:-$(mktemp)}
[[ -n ${EML_OUT:-} ]] || trap 'rm -f "$eml"' EXIT
{
  if [[ $dmarc != none ]]; then
    printf 'Authentication-Results: mx.%s; spf=pass smtp.mailfrom=%s; dkim=pass header.d=%s; dmarc=%s header.from=%s\r\n' \
      "${to#*@}" "$domain" "$domain" "$dmarc" "$domain"
  fi
  printf 'From: Nordwind Rechnungsversand <%s>\r\n' "$from"
  printf 'To: %s\r\n' "$to"
  printf 'Subject: %s\r\n' "$subject"
  printf 'Message-ID: <%s.%s@%s>\r\n' "$(date +%s)" "$RANDOM$RANDOM" "$domain"
  printf 'Date: %s\r\n' "$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S +0000')"
  printf 'MIME-Version: 1.0\r\n'
  printf 'Content-Type: multipart/mixed; boundary="%s"\r\n\r\n' "$boundary"
  printf -- '--%s\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nSehr geehrte Damen und Herren, anbei Ihre Rechnung.\r\n' "$boundary"
  printf -- '--%s\r\nContent-Type: application/pdf; name="%s"\r\nContent-Transfer-Encoding: base64\r\n' "$boundary" "$name"
  printf 'Content-Disposition: attachment; filename="%s"\r\n\r\n' "$name"
  base64 < "$pdf" | tr -d '\r\n' | fold -w 76 | awk '{ printf "%s\r\n", $0 }'
  printf -- '--%s--\r\n' "$boundary"
} > "$eml"

if [[ -n ${EML_OUT:-} ]]; then
  echo "wrote $eml"
else
  curl -sS --noproxy '*' "$smtp" --mail-from "$from" --mail-rcpt "$to" --upload-file "$eml"
  echo "mailed $name to $to (dmarc=$dmarc)"
fi
