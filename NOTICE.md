# Attribution and distribution

BetterGI is the source of the child-session approach used here, particularly its ChildSession service
(https://github.com/babalae/better-genshin-impact), licensed by its authors under
GPL-3.0. ParaDesk (https://github.com/sinpoce/ParaDesk), MIT licensed by its authors, is related work and was also consulted locally as a Windows child-session reference.
These reference checkouts are excluded from this repository and all release bundles.
This repository implements its own MCP adapter, lifecycle and Windows API integration;
its MIT license does not relicense either reference project.

OpenAI's Computer Use executable is discovered in the user's installation and is
not redistributed. OpenAI, ChatGPT and Codex names identify compatibility; this
project is independent and is not endorsed by OpenAI.

Self-contained bundles include Microsoft .NET and Windows Desktop runtime files.
Their exact runtime-pack license and third-party notices are included in each
bundle's `better-computer-use/third-party/` directory. Windows RDP components are
provided by the operating system, not bundled here.
