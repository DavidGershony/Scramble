# Bot message timestamp sorting issue

## Status: Code correct — likely bot-side issue

## Problem

Messages in bot chats appear sorted incorrectly. User reports the display order seems based on the gift-wrap timestamp rather than the inner rumor's `created_at`.

## Analysis (complete)

The client code path is correct:
- `UnwrapGiftWrapAsync` (NostrService.cs:1325-1347) extracts `rumor.created_at` and sets `CreatedAt` on the `NostrEventReceived`
- `HandleBotMessageEventAsync` (MessageService.cs:935) uses `nostrEvent.CreatedAt` for the message `Timestamp`
- `GetMessagesForChatAsync` (StorageService.cs:617) orders by `Timestamp DESC`

Most likely cause: the MCP nostr plugin randomizes the rumor's `created_at` for NIP-59 privacy, so messages from the bot arrive with non-monotonic timestamps by design.

## Conclusion

No client-side fix is needed. If ordering matters the bot should use monotonically increasing `created_at` values. If desired, the client could fall back to DB insertion order when timestamps are identical — but this is a low-priority UX concern.
