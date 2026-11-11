# GilDom

⚠️ GilDom is in beta. Expect changes, and keep a copy of your secret.

## How to install

This installs the GilDom plugin. Both players do this on their own game.

1. Open Dalamud's settings.
2. Open Experimental.
3. Add this custom plugin repository:

https://raw.githubusercontent.com/FricOwO/GilDom/master/gildom.json

4. Install GilDom from the plugin list.
5. Install Moodles too. GilDom uses it for the status text.

Dalamud plugin for a linked gil roleplay. One person is the sub and the other is the dom. The number drawn on the sub's gil counter changes. Gil stored by the game server does not.

`/gildom` opens the window. The first time a character reaches the server, the server assigns that character an 8-character friend code. Copy it, paste your partner's code, choose "I will be the dom" or "I will be the sub", and send the request. Accepting locks the pair. A sub has one active dom. A dom can have several subs. Nicknames stay on your own computer.

The gear at the top right opens Settings. There you can show, copy, replace, or request a new secret. A new secret keeps the same friend code and the same links. The previous secret stops working. **Cap the gil counter** is there too. While you are the sub and that box is on, the gil counter shows your allowance instead of the full amount.

## Handing gil over

The dom sets a tax percent and an allowance. Earned gil adds the tax, and anything above the allowance, to the amount still to hand over. On the dom's Collect tab, enter an amount and press Drain. If the dom is nearby, the sub's client opens a trade with the dom's current character. The sub still presses OK on the trade. Amounts over 1,000,000 gil are split across repeated trades. The dom presses Received when the hand-over is done, and that clears the owed amount. The plugin does not take gil by packet, and it does not rewrite shop or trade windows.

During a drain, the dom's character name and home world are held in the server's memory so the sub can find that character. They are not written to the save file.

## Moodles

The sub has to allow moodles from other plugins. GilDom applies its moodle as sticky, so Moodles keeps it in place. A right-click in Moodles can still take it off. Paste a moodle copied from the Moodles plugin, then press Save.

Tokens in the title or text:

- `<gilowed>` is the amount still to hand over.
- `<taxgil>` is the tax percent, such as `10%`.
- `<maxgil>` is the allowance. It is blank when the allowance is 0.

## The server

Each plugin calls `POST /sync`. The call carries that character's secret, the friend codes it wants, and any role, tax, moodle, or gil change since the last call. The server stores the secret as a SHA-256 hash and keeps the latest row for each friend code. It does not store character names or worlds. A quiet client calls again after 5 seconds. A change is sent immediately. A failed call retries after 3 seconds.

The plugin connects to `https://gildom.fly.dev/sync`. There is no address to type.

## License

[MIT](LICENSE)
