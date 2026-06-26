# QuickBooks localhost bridge settings (TEMPLATE).
#
# deploy_servicehost.ps1 copies this to the install share as "bridge.settings.psd1"
# IF one is not already there. The real, secret-bearing bridge.settings.psd1 lives
# ONLY on the share (and is git-ignored) — set QB_BRIDGE_TOKEN there once and every
# workstation that runs install_service_host.ps1 picks it up as a machine env var.
#
# All three MUST match the app server:
#   QB_BRIDGE_TOKEN  = the app server's QUOTE_MODULEV2_QB_BRIDGE_TOKEN (shared secret)
#   QB_BRIDGE_ORIGIN = the app-server origin (scheme://host:port), echoed via CORS
#   QB_BRIDGE_PORT   = the port in the app server's QUOTE_MODULEV2_QB_BRIDGE_URL
#
# The bridge rejects every request with 403 until QB_BRIDGE_TOKEN is non-empty.
@{
    QB_BRIDGE_TOKEN  = ''
    QB_BRIDGE_ORIGIN = 'http://APPSRV01:8742'
    QB_BRIDGE_PORT   = '8788'
}
