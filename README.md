# Yap

Self hosted chat inspired by Discord aesthetics

![Screenshot](screenshot.png)


## Run with Docker

```bash
docker run -d --name yap -p 5221:8080 -v ./uploads:/app/wwwroot/uploads -v ./config:/app/Data ghcr.io/urza/yap:latest
```

There are two volumes:
- "uploads" which holds uploaded media
- "data" which contains configuration (appconfig.json) and SQLite db (if you opt for using db)

Access at `http://localhost:5221` - it's up to you how to make this accessible for others. For example use some reverse proxy like nginx proxy manager - https://nginxproxymanager.com/

## Features

- **No registration required** - Just log in with username
- **Database optional** - Everything can be ephemeral and live only in memory (wiped on app reset) or you can use SQLite/Postgres for persistence
- **Customizable labels in config** - make it fun or serious
- **Emoji support** - Beautiful Twemoji rendering
- **Dark theme hardcoded** - Discord-inspired UI, because I know what's best for you
- **Multiple rooms/channels** - Create and switch between chat rooms (admin can create new)
- **Direct messages** - Private conversations between users
- **Image sharing** - Upload image(s) and see them in inline gallery
- **Message actions** - Discord-style hover popup with reactions, edit, delete
- **Reactions** - React to messages with emojis
- **Tab notifications** - Unread count in browser tab + audio notifications
- **Typing indicators** - See who's typing with customizable message
- **Mobile responsive** - Works great on all devices with collapsible sidebar
- **PWA installable** - Add to home screen on mobile, install as app on desktop

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is open source and available under the MIT License.
