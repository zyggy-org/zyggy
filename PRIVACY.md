# Privacy Policy — Zyggy

*Last updated: 8 October 2026*

Zyggy is a personal AI assistant run by its owner for his own use. This policy covers the Zyggy LinkedIn app, the
developer application that lets Zyggy publish posts to the owner's own LinkedIn profile.

## Who is responsible

Zyggy is operated by Digiverse (Belgium), [digiverse.be](https://digiverse.be), for its owner's personal use. Zyggy is
not a public service: no one other than the owner can sign in to it or use it.

## What the LinkedIn app accesses

When the owner connects his LinkedIn account, the app asks LinkedIn for three permissions only:

| Permission | Used for |
|---|---|
| `openid`, `profile` | Reading the owner's name and LinkedIn member id, to confirm the right account is connected |
| `w_member_social` | Publishing a post to the owner's own profile, after he has approved that exact text |

The app does not read the feed, connections, messages or profiles of other members, and it does not collect any data
about other LinkedIn members.

## What is stored, where, and for how long

- **The access token** LinkedIn issues is stored only on the owner's own server, in a file readable by the Zyggy
  account alone. It is never logged, shared or sent anywhere except to LinkedIn's API. It expires after 60 days and is
  deleted when the owner disconnects the app.
- **Published posts:** the text, date and LinkedIn id of each post the owner approved are kept on the same server as a
  record of what was published. Drafts that were not published are not kept.
- **No other storage.** Nothing is stored in third-party databases, analytics tools or advertising systems.

## Sharing

Data is never sold, rented or shared with third parties. The only party Zyggy sends data to is LinkedIn itself, through
its official API, to publish the posts the owner approved. Drafting is done with Anthropic's Claude, on the owner's own
subscription, under Anthropic's commercial terms.

## Your rights

Because the app only processes the owner's own account, the person whose data is processed is the owner himself. He
can revoke Zyggy's access at any time in LinkedIn under *Settings → Data privacy → Permitted services*, which makes the
token useless immediately.

If you believe Zyggy has processed data about you, contact us through [digiverse.be](https://digiverse.be) and we will
answer within 30 days. You also have the right to lodge a complaint with the Belgian Data Protection Authority
([dataprotectionauthority.be](https://www.dataprotectionauthority.be)).

## Changes

Changes to this policy are made in this file. Its history on GitHub shows every change and its date.
