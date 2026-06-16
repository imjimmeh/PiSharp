# Changelog

All notable changes to PiSharp are documented here.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Versioning: [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-06-08

### Added
- Interactive Terminal UI (TUI) for conversational coding-agent workflows
- Print, JSON, and RPC output modes via `pisharp` CLI
- Multi-provider LLM support: Anthropic, OpenAI, Google Gemini/Vertex, AWS Bedrock, Mistral
- Built-in coding tools: `read`, `write`, `edit`, `bash`, `grep`, `find`, `ls`
- Durable JSONL session persistence with continuation, branching, and compaction
- Native .NET extension architecture (DLL plugins via collectible assembly loading)
- TypeScript/JavaScript extension bridge (backward-compatible with Pi JS packages)
- Live session WebSocket server (`/ws`) and health endpoint (`/health`)
- Multi-agent coordination extension

[Unreleased]: https://github.com/imjimmeh/PiSharpio/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/imjimmeh/PiSharpio/releases/tag/v1.0.0
