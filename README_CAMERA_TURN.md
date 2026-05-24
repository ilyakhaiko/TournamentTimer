# TournamentTimer Camera and TURN Notes

This guide explains how TournamentTimer can use ICE/TURN settings for runner cameras.

TournamentTimer does not run a TURN server by itself. It only reads TURN configuration and passes it to camera pages.

## What changes with TURN

Runner cameras use WebRTC.

WebRTC may work directly on LAN, but remote events can fail because of NAT, VPN, firewall rules, or mobile networks.

TURN is a relay server that can help WebRTC connect in difficult networks.

TournamentTimer can provide ICE server configuration to:

```text
runner-camera.html
camera-source.html
admin preview
OBS camera browser sources
```

## Endpoint

TournamentTimer can expose camera ICE settings through:

```text
GET /api/runs/<runId>/media/ice-servers
```

If keys are enabled, include a valid key.

Example:

```text
/api/runs/local-test-run/media/ice-servers?key=VIEW_KEY
```

## timer-settings.json example

Example configuration:

```json
{
  "runId": "local-test-run",
  "adminKey": "CHANGE_ADMIN_KEY",
  "runKey": "CHANGE_RUN_KEY",
  "viewKey": "CHANGE_VIEW_KEY",

  "CameraIceTransportPolicy": "all",
  "CameraIceServers": [
    {
      "Urls": [
        "stun:stun.l.google.com:19302"
      ]
    },
    {
      "Urls": [
        "turn:turn.example.com:3478?transport=udp",
        "turn:turn.example.com:3478?transport=tcp",
        "turns:turn.example.com:5349?transport=tcp"
      ],
      "Username": "CHANGE_TURN_USERNAME",
      "Credential": "CHANGE_TURN_PASSWORD"
    }
  ]
}
```

## CameraIceTransportPolicy

```text
all
  Normal mode.
  WebRTC tries direct/STUN first and can fall back to TURN.

relay
  Test mode.
  Forces video to go through TURN only.
```

For a real event, usually use:

```json
{
  "CameraIceTransportPolicy": "all"
}
```

To test that TURN really works, use:

```json
{
  "CameraIceTransportPolicy": "relay"
}
```

If camera works in `relay` mode across mobile internet or VPN, TURN is working.

## Check server configuration

After changing `timer-settings.json`, restart TournamentTimer Server.

Check:

```text
/api/server-info?key=ADMIN_KEY
```

Expected camera summary example:

```json
{
  "cameraIce": {
    "serverCount": 2,
    "turnEnabled": true,
    "iceTransportPolicy": "all"
  }
}
```

Then check:

```text
/api/runs/local-test-run/media/ice-servers?key=VIEW_KEY
```

It should return JSON with `iceServers`.

## Camera test

Basic test:

```text
runner:
  Connect in Runner UI
  Start camera

admin/viewer:
  open admin preview or camera source URL
```

Stronger network test:

```text
runner on one network
viewer on another network
or phone over mobile internet
or VPN enabled
```

If `relay` mode works, TURN is likely configured correctly.

## What the server admin must configure

A typical TURN setup uses coturn or a managed TURN service.

Common requirements:

```text
coturn or managed TURN service
UDP/TCP 3478
TCP/TLS 5349 if turns is used
UDP relay range, for example 49152-65535
domain such as turn.example.com
TURN username
TURN password
firewall rules
```

Exact configuration depends on VPS provider, firewall, domain, and TLS setup.

TournamentTimer only consumes the final TURN URL, username, and credential.

## Common problems

### Camera works on localhost but not on remote server

Browser camera access and WebRTC behavior can depend on HTTPS, hostname, and network.

Use HTTPS for real remote deployments when possible.

### Camera is online but OBS shows nothing

Check:

```text
camera source URL copied from admin panel
URL does not use localhost unless OBS is on the same machine
viewKey is included if required
Browser Source was refreshed
TURN is configured for difficult networks
```

### TURN relay mode fails

Check:

```text
TURN URL
username
credential
VPS firewall
UDP relay range
coturn logs
TLS certificate if using turns
```

### Direct mode works but relay mode fails

The camera may be working through direct/STUN, but TURN is not configured correctly.
