# QuickBooks localhost bridge settings (TEMPLATE).
#
# deploy_servicehost.ps1 copies this to the install share as "bridge.settings.psd1"
# IF one is not already there. The real, secret-bearing bridge.settings.psd1 lives
# ONLY on the share (and is git-ignored) — set QB_BRIDGE_TOKEN there once and every
# workstation that runs install_service_host.ps1 picks it up as current-user env
# vars. Run the installer as the same normal interactive user that runs QuickBooks.
#
# All three MUST match the app server:
#   QB_BRIDGE_TOKEN  = the app server's QUOTE_MODULEV2_QB_BRIDGE_TOKEN (shared secret)
#   QB_BRIDGE_ORIGIN = the app-server origin (scheme://host:port), echoed via CORS
#   QB_BRIDGE_PORT   = the port in the app server's QUOTE_MODULEV2_QB_BRIDGE_URL
#
# The bridge rejects every request with 403 until QB_BRIDGE_TOKEN is non-empty.
@{
    QB_BRIDGE_TOKEN  = ''
    QB_BRIDGE_ORIGIN = 'https://act.proto-cam.local:8743'
    QB_BRIDGE_PORT   = '8788'
    # Exact native QuickBooks item names; configure before discounted sends.
    QB_QUOTE_SUBTOTAL_ITEM = ''
    QB_QUOTE_DISCOUNT_ITEM = ''
    # Set once on the share. Only the named PC installs the watcher settings.
    QB_OPPORTUNITY_COMPUTER = ''
    QB_OPPORTUNITY_COMPANY = ''     # Full .QBW path as seen by that workstation.
    QB_OPPORTUNITY_SINCE = ''       # YYYY-MM-DD in the QuickBooks company clock.
    QB_OPPORTUNITY_TOKEN_FILE = ''  # Restricted file containing the Workbench conversion token.
}
