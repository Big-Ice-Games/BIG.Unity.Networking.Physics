# BIG.Unity.Networking.Physics

Physics-play toolkit of the BIG ecosystem: a first-person **physical player with stretchy sticky hands**, **hanging ropes / lamps**, **breakable hinges** (doors, hatches), **grabbable ragdolls** (NPC bodies), plus their **co-op networking** on [BIG.Unity.Networking](https://github.com/Big-Ice-Games/BIG.Unity.Networking).

Born in the PhysicTest prototype (the sandbox for up to 4 players is still there: `Prototype > Build Network Prototype`).

## Install

`Packages/manifest.json`:

```json
"com.bigicegames.big.unity": "https://github.com/Big-Ice-Games/BIG.Unity.git",
"com.bigicegames.big.unity.networking": "https://github.com/Big-Ice-Games/BIG.Unity.Networking.git",
"com.bigicegames.big.unity.networking.physics": "https://github.com/Big-Ice-Games/BIG.Unity.Networking.Physics.git",
"com.firstgeargames.fishnet": "https://github.com/Big-Ice-Games/FishNet.git?path=Assets/FishNet#big"
```

Also needs the **Input System** package (player input). The core works without the networking packages; the network part compiles only when FishNet is present (`BIG_FISHNET`).

| Assembly | Namespace | What |
|---|---|---|
| `BIG.Unity.Networking.Physics.Core` | `BIG.Unity.Physical` | Offline physics: player, hands, rope, hinge, mount, ragdoll. No networking. |
| `BIG.Unity.Networking.Physics` | `BIG.Unity.Physical.Net` | Networking of all of the above (FishNet + BIG.Unity.Networking). |
| `…Core.Editor`, `…Editor` | `BIG.Unity.Physical.Editor` | Create / Make Networked menus, Build buttons. |

## Create things (single player)

**GameObject > BIG > Physics >**

| Item | What you get |
|---|---|
| **Physical Player** | `PhysicalPlayer` (force-driven capsule: walk, sprint, jump; optional falling over), camera, two `StretchyHand`s on `PlayerHands`, `LocalInputReader` (WASD, mouse, LMB/RMB = hands, scroll = hold distance), `PlayerBodyVisual` (simple alien body; your own only casts a shadow), `HandsDebugHud` (IMGUI dev readout — remove it and draw your own HUD from `StretchyHand`). |
| **Hanging Rope (lamp)** | `HangingRope`: set length, links, end object (any object, or a ball), mount strength → **Build**. Links are invisible capsules on joints drawn as one rope (`RopeRenderer`); a tether carries the load; `BreakableMount` tears it off the ceiling. |
| **Hinged Door** | `BreakableHinge`: edge (left/right/top/bottom/custom), limits, friction, mass, strength → **Build** (HingeJoint + BreakableMount). Works on any object. |
| **Simple Ragdoll** | 25 kg primitive ragdoll with a `Ragdoll` on the root. No brain: write one that reads `Ragdoll.simulate`, `IsHeld`, `SecondsSinceHeld`, `PartHit`, and pushes `Ragdoll.core` (PhysicTest's `Zombie` is the example). |

Any `Rigidbody` is grabbable as is. A hand pulls with at most 250 N (lifts ~25 kg); forces from several hands / players add up.

## Make it networked

**GameObject > BIG > Networking > Physics >** (on the selection; adds the physics component and builds it first if missing)

| Preset | Adds |
|---|---|
| **Physical player** | NetworkObject, NetworkTransform, NetworkBody (`PhysicalPlayer` profile), NetworkPlayer, `NetworkPhysicalPlayer`; camera / listener / input off until the network assigns roles. Save as a prefab → `PlayerSpawner`. |
| **Grabbable prop** | Rigidbody (+ collider), NetworkObject, NetworkTransform, NetworkBody (`PhysicalProp` profile), `NetworkGrabbable`. |
| **Hanging rope (lamp)** | One NetworkObject on the rope, every link + the end a grabbable body, `NetworkBreakableMount`. **Rebuild re-applies it.** |
| **Hinged (door)** | Grabbable body + `NetworkBreakableMount`. |
| **Ragdoll NPC** | One NetworkObject, every part a grabbable body, `NetworkRagdoll`. |

Profiles are created in `Assets/BIG.Networking/Profiles` (`PhysicalProp`, `PhysicalPlayer`): owner authority + owner physics, interpolation 2, **no extrapolation** (springy held things change direction all the time — guessing ahead snaps back).

### How it plays over the network — "everyone simulates themselves and what they hold"

| Thing | Simulated by | Others see |
|---|---|---|
| Your body and both hands | you (no lag) | interpolated body; hands from your state, every tick |
| A prop / rope / door / ragdoll nobody holds | the host | interpolated |
| A thing you grab | you — the hand asks for its ownership (`NetworkGrabbable`, max 6 m from your avatar); back to the host once it rests after you let go | interpolated |
| A thing someone ELSE simulates (they hold it, or ownership is on its way) | them | your hand still pulls it: the force goes to them (`RemoteForces`, routed by the host) and they apply it |
| Another player you grab | that player | your pull goes to them the same way |

- **Mount tears** (`NetworkBreakableMount`) and **ragdoll deaths** (`NetworkRagdoll`) are decided where the object is simulated at that moment and sent to everyone through the host, buffered for late joiners.
- **Ragdoll brains** run only where `Ragdoll.simulate` is true — whoever simulates the core body.
- **Hand anchors** travel as `NetAnchors` ids: avatar part (player id), networked body (object id + component), static scenery (scene + names).
- **Dev overlay** module *Physics net* (F1): who simulates what your hands hold; other players' hands as resolved here (red = wrong object).

Host migration of physics objects isn't supported yet (turn it off in the NetworkConfig, or the level resets for the remaining players).

## Hooks for your own code

`StretchyHand` statics, set by the network layer — single player leaves them null:

- `IsHeld(Rigidbody)` — any hand here or remote holds it.
- `Attached` / `Released` events — a hand simulated here stuck to / let go of a body.
- `IsRemoteBody`, `RemoteForce` — how pulls on bodies simulated elsewhere are routed.
- Per hand: `CurrentState`, `Tense`, `Load`, `Strain`, `AnchorBody`, `AttachedName` (for your HUD).
