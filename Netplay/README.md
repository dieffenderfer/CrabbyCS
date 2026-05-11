# Netplay & Buddy List

AIM-style buddy list + Switch-style friend codes for MouseHouse. Friends
you've added show up in a yellow buddy-list panel with online / idle /
away / invisible / offline status and free-text away messages. The
underlying transport is broker-mediated end-to-end-encrypted MQTT — no
hosted server, no peer-to-peer hole-punching, no random discovery.

> **Status:** bedrock layer (identity, friend codes, buddy list,
> presence, friend requests, status, challenge-to-play stubs). Full
> per-game netplay (golf head-to-head, chess co-op) lives on top of
> this and is intentionally out of scope for the first round — those
> ship via dedicated follow-up commits.

## Goals

| Goal | How we get it |
|---|---|
| No random discovery | Friends-only. You only see / connect to people whose 12-char friend code you've exchanged out-of-band (text, email, AirDrop). |
| Unguessable codes | 12 chars of Crockford base32 → 60 bits of entropy. Generated with `RandomNumberGenerator.GetBytes`. |
| End-to-end encryption | libsodium `crypto_box` (Curve25519 + XSalsa20 + Poly1305) via `NSec.Cryptography`. The broker sees only ciphertext + opaque topic ids. |
| Zero hosting cost | All signaling and message routing goes through a free public MQTT broker over TLS (`broker.hivemq.com:8883`). No server to deploy. |
| Identity stable across IP changes | A long-term Curve25519 keypair (not the friend code) is the cryptographic identity. The friend code is a separate, typeable, rotatable handle. |

## Why MQTT instead of "real" P2P

Three honest reasons:

1. **Zero ops.** Cloudflare Workers + Durable Objects, a STUN/TURN
   server, or a custom rendezvous endpoint all require something
   hosted. MQTT public broker requires nothing.
2. **NAT traversal is free.** Both clients open outbound TLS to the
   broker. No port-forward dance, no symmetric-NAT failure case.
3. **MQTT pub/sub maps directly to AIM-style presence.** A retained
   message on a per-friend presence topic is *exactly* "this is what
   the friend was last seen doing." Subscriptions = the buddy list.

The trade-off is that all traffic transits the broker. Since every
payload is sealed with a libsodium box against the recipient's public
key, the broker sees:

- Opaque topic ids (`sha256(friend_code)[:16]`).
- Encrypted blobs.
- Connection timing.

The broker cannot see plaintext, friend codes, names, IPs, or message
content. Plaintext only exists on the two endpoints.

For the actual real-time golf head-to-head or chess co-op, MQTT pub/sub
is borderline-fine for chess (turn-based) and would need work for golf
(real-time physics). That's why those are stubbed in this round —
adding a Cloudflare Worker for low-latency signaling + ENet/WebRTC for
the actual game traffic is the natural follow-up if/when the
latency requirement bites.

## Identity, friend codes, and the mapping between them

| Thing | Where it lives | Lifetime | Purpose |
|---|---|---|---|
| Long-term keypair | `identity.json` in the save dir | Permanent (deleting it = becoming a new user) | Cryptographic identity. `crypto_box` secret key + matching public key. |
| Friend code | `identity.json`, alongside the keypair | Permanent by default, but rotatable | Human-shareable handle. 12 chars Crockford base32. |
| Topic id | Derived: `sha256(friend_code)[:16]` | Follows the friend code | Opaque identifier the broker sees instead of your friend code. |

The friend code is intentionally **decoupled from the keypair** for
two reasons:

- Codes need to be typeable; raw public keys (32 bytes / 64 hex) are
  not. A 12-char code is 60 bits, well past the work factor anyone
  would brute force.
- Codes should be rotatable. If you mistype yours into a public
  channel, you can regenerate without losing your keypair (and
  therefore without your existing friends having to re-trust you).
  This isn't wired into the UI for v1 but the data model supports it.

## Topic layout

```
mhouse/v1/<topic_id>/inbox        ← request / accept / chat / challenge
mhouse/v1/<topic_id>/presence     ← retained; status + away message
```

`<topic_id>` is always `sha256(friend_code)[:16]`. Every user
**subscribes** to their own inbox + their friends' presence topics,
and **publishes** to their own presence + their friends' inboxes.

Each `inbox` message is a sealed-box ciphertext keyed to the
recipient's public key. The plaintext payload is a JSON envelope
that distinguishes message types:

```json
{ "kind": "request",   "from_code": "...", "from_pubkey_hex": "...", "from_name": "..." }
{ "kind": "accept",    "from_code": "...", "from_pubkey_hex": "..." }
{ "kind": "challenge", "game": "golf"|"chess", "nonce": "..." }
{ "kind": "chat",      "text": "..." }
```

`presence` messages are signed by the sender (so the broker can't
inject forged presence) and contain `{ status, away_message,
last_seen }`.

## Threat model

Honest-but-curious broker (HiveMQ):

- **Sees** topic ids, ciphertext sizes, connection timing.
- **Cannot see** plaintext, friend codes, names, IPs, or message
  content.
- **Cannot impersonate** users (no matching keypair).
- **Could** correlate inbound and outbound topic activity to infer
  who-talks-to-whom *if* it monitors all of MouseHouse's traffic —
  this is a tracking risk, not a content-leak risk. Mitigation
  (not implemented in v1): pad messages to a uniform size, batch
  inbox writes.

Attacker who knows your friend code (because you posted it
somewhere):

- **Can** send you spam friend requests (UI gate via Accept / Reject).
- **Can** subscribe to your presence topic and watch your status
  change. *This is by design* — it's the same property AIM had.
- **Cannot** see your inbox plaintext (sealed-box to your public
  key, which they don't have until you accept their request).
- **Cannot** impersonate you to your existing friends.

Attacker who **does not** know your friend code: cannot find you.
60-bit unguessable + zero discovery side-channel.

## What's in this folder

This README + (eventually) the `BuddyList.cs`, `NetClient.cs`,
`Identity.cs`, and friends-related UI live under `Net/Buddies/` and
`UI/BuddyList/` (in-tree, not under `Netplay/`) — this folder is
just the design doc. If we add Cloudflare Worker code or a STUN
server config for follow-up real-time work, it'll land here too.

## Libraries

| Library | Why | License |
|---|---|---|
| [`MQTTnet`](https://github.com/dotnet/MQTTnet) 4.x | Mature, .NET-native MQTT client with TLS support. | MIT |
| [`NSec.Cryptography`](https://nsec.rocks) 24.x | Audited libsodium bindings. AOT-safe, easy `crypto_box`. | MIT |

Both are already-in-scope for .NET 10 (no native rebuild needed
beyond what the package brings).

## Hosted broker

Default: `broker.hivemq.com:8883` (TLS). Free, anonymous, has been
running for years. If it ever goes down, swap in any other public
broker — `test.mosquitto.org:8883`, `mqtt.eclipseprojects.io:8883`,
or a paid one. Configured via a constant in `NetConfig.cs`; not
user-configurable so a malicious config file can't redirect traffic
through an attacker's broker.
