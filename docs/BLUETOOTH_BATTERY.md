# Bluetooth battery sources

PopBat reads standard BLE Battery Service directly. For connected, paired Classic
audio devices it also checks the standard Fast Pair Message Stream RFCOMM service.
This is a generic service check, with no brand/model allowlist or online lookup.
Devices without either supported battery source can remain unknown.

The Classic reader uses the current Bluetooth connection name as an explicitly
labelled identification fallback. It does not independently verify a manufacturer
or model string through this protocol. No Windows battery properties are read.

Only the exact Bluetooth service UUID is opened, using authenticated Bluetooth
encryption. PopBat receives device battery events and sends one fixed read-only
active-components request (`03 05 00 00`). No other Fast Pair commands are sent.
No pairing, account keys, ringing, audio streams, configuration, radio scanning,
Google SDK, cloud connection, API key or paid API is involved.

The status message can contain a peer earbud's last reported percentage even when
that earbud is absent. The active-components response determines which earbud
contributes to the main percentage. With both active, the lower valid earbud value
is shown; the case is excluded. Without verified active state there is no aggregate
percentage, and component reports are labelled accordingly. Multi-component
headsets do not contribute to remaining-runtime estimates.

Queries run only at startup or explicit refresh. Each Classic round has a six
second UI budget, a maximum of four candidates and one retained native round if
Windows takes longer to finish. Each device has a five second deadline; status
reception has a 1.2 second deadline, 16-frame limit, 512-byte frame limit and
2048-byte total limit. All sockets/readers/services are closed after the round.
An outstanding native request blocks further Bluetooth rounds until completion.
No headset listener stays running between refreshes. Only bounded failure timing
metadata is cached; battery readings are obtained anew each round.

## Protocol references and attribution

Original PopBat C# implementation based on the published Google Fast Pair
specifications, accessed 2026-10-22:

- Google, [Message Stream](https://developers.google.com/nearby/fast-pair/specifications/extensions/messagestream)
- Google, [Device information](https://developers.google.com/nearby/fast-pair/specifications/extensions/deviceinformation)
- Google, [Battery Notification](https://developers.google.com/nearby/fast-pair/specifications/extensions/batterynotification)

Google's documentation text is licensed under
[Creative Commons Attribution 4.0](https://creativecommons.org/licenses/by/4.0/).
The explanations here are adapted summaries; no Google sample code or SDK is
included. Use of the public wire format does not imply Google certification.
