# SDD ledger — plan: docs/superpowers/plans/2026-08-15-daemon-runtime-hosting.md

BASE = ac49905

Task 1: minor (deferred): tests use direct ServerUiBridge construction rather than the brief-mandated raw-WebSocket harness; the same ResolveUiAsync path the wire ui_response handler uses (PiServerWebSocketHandler.cs:441-443) is exercised, new surface covered. Ruling: acceptable, defer to final review.
