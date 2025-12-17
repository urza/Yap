# Yap Configuration Guide

## Overview

Yap supports configurable project/room names and randomized UI text variations to make the chat experience more entertaining.

## Configuration File

All configuration is in `Yap/appsettings.json`.

## Configuration Structure

### Basic Settings

```json
{
  "ChatSettings": {
    "ProjectName": "Yap",
    "RoomName": "lobby",
    "FunnyTexts": {
      // Text variation collections
    }
  }
}
```

### Text Collections

#### Welcome Messages
Displayed when users first arrive. Use `{0}` as placeholder for project name.

```json
"WelcomeMessages": [
  "welcome to {0}",
  "you ready?",
  "you found {0}"
]
```

#### Join Button Texts
Random text for the join button:

```json
"JoinButtonTexts": [
  "lessgo",
  "slide in",
  "hop on",
  "lock in"
]
```

#### Username Placeholders
Placeholder text for the username input:

```json
"UsernamePlaceholders": [
  "drop your @",
  "who dis?",
  "pick your fighter"
]
```

#### Message Placeholders
Placeholder text for the message input:

```json
"MessagePlaceholders": [
  "say hi...",
  "spill the tea...",
  "drop a hot take..."
]
```

#### Connection Statuses
Connection state indicators:

```json
"ConnectionStatuses": {
  "Connected": ["online"],
  "Disconnected": ["offline"]
}
```

#### System Messages
User join/leave messages. Use `{0}` for username:

```json
"SystemMessages": {
  "UserJoined": [
    "{0} just dropped",
    "{0} pulled up",
    "{0} entered the chat"
  ],
  "UserLeft": [
    "{0} dipped",
    "{0} ghosted us",
    "{0} went to touch grass"
  ]
}
```

#### Typing Indicators
Messages shown when users are typing:

```json
"TypingIndicators": {
  "Single": [
    "{0} is cooking..",
    "{0} is yapping.."
  ],
  "Double": [
    "{0} and {1} are cooking..",
    "{0} and {1} causing chaos.."
  ],
  "Multiple": [
    "{0}, {1} and more going crazy..",
    "everyone typing their hot takes.."
  ]
}
```

#### Other UI Elements

```json
"OnlineUsersHeader": [
  "the gang ({0})",
  "squad check ({0})"
],
"RoomHeaders": [
  "# {0}",
  "{0} vibes only"
]
```

## How It Works

1. **Random Selection**: Each UI element randomly selects from its configured text variations
2. **Fallback Values**: If a configuration is missing, sensible defaults are used
3. **Placeholder Formatting**: `{0}`, `{1}` are replaced with actual values (username, count, etc.)

## Environment-Specific Configuration

Create `appsettings.Development.json` for development overrides:

```json
{
  "ChatSettings": {
    "ProjectName": "DevYap",
    "FunnyTexts": {
      "WelcomeMessages": [
        "Welcome to {0} DEV MODE!"
      ]
    }
  }
}
```

Create `appsettings.Production.json` for production:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning"
    }
  }
}
```

## Adding New Variations

Simply add more strings to any array in the configuration. The system automatically includes them in random selection.

## Technical Implementation

The `ChatConfigService` class handles:
- Loading configuration from `IConfiguration`
- Random selection of text variations
- Formatting placeholders with actual values
- Providing fallback defaults

The service is registered as scoped in `Program.cs` and injected into the Chat component.
