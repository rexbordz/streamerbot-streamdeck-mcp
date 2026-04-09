# Streamer.bot → Stream Deck (MCP Integration)

Control your Stream Deck using Streamer.bot triggers.

This extension allows Streamer.bot to execute Stream Deck actions by leveraging Elgato’s **MCP (Multi Control Protocol)** introduced in Stream Deck 7.4.0.

---

## ✨ Features

- Trigger Stream Deck buttons using Streamer.bot — including plugin actions
- Extend Streamer.bot beyond its native action features
- Works with your existing Stream Deck setup

---

## ⚡ Requirement (Important)

This project **requires Stream Deck 7.4.0 or newer**.

It is built on top of Elgato’s **MCP (Multi Control Protocol)**, which introduces a system for:

- Retrieving executable actions from Stream Deck  
- Executing actions programmatically via its local server  

This extension relies entirely on MCP and communicates with Stream Deck through that server layer.

Additionally, **[Node.js](https://nodejs.org/) is required**, as recommended by Elgato for MCP-based integrations.

👉 Learn more about MCP setup:  
https://www.elgato.com/us/en/explorer/products/stream-deck/sd-mcp-setup/

If you are on an older version of Stream Deck or do not have Node.js installed, this will **not work**.

---

## 🚀 Quick Start

For full setup instructions, see:  
👉 [Installation](https://github.com/rexbordz/streamerbot-streamdeck-mcp/wiki/I.-Installation)

---

## 🧠 How It Works

Streamer.bot trigger → extension → Stream Deck (MCP server) → action execution

Instead of being limited to Streamer.bot’s built-in actions:

- Streamer.bot reacts to events (e.g., Twitch Chat, Voice Command, Donation)
- The extension sends a request through MCP
- Stream Deck executes the corresponding action

---

## 📚 Documentation

For full setup, configuration, and advanced usage:

👉 [See the Wiki](https://github.com/rexbordz/streamerbot-streamdeck-mcp/wiki)

---

## 💡 Use Cases

- Trigger Stream Deck plugin actions from Streamer.bot  
- Access functionality not natively available in Streamer.bot  
- Build advanced cross-tool automation pipelines  

---

## ℹ️ Notes

- Stream Deck must be running
- Actions must already exist in your Stream Deck MCP Page
- Requires Stream Deck 7.4.0+ with MCP support

---

## ⚠️ Disclaimer

This project is **not affiliated with, endorsed by, or supported by** Streamer.bot or Elgato Stream Deck.

It is an independent project built from personal experimentation with MCP.  
Use at your own discretion.

---

## 🔧 Status

Active development — improvements and refinements ongoing.
