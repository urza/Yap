# BlazorChat Configuration Guide

## Overview
The BlazorChat client now supports configurable project/room names and random funny text variations for various UI elements to make the chat experience more entertaining.

## Configuration File
The configuration is stored in `BlazorChat.Client/wwwroot/appsettings.json` (and `appsettings.Development.json` for development environment).

## Configuration Structure

### Basic Settings
```json
{
  "ChatSettings": {
    "ProjectName": "BlazorChat",    // The name of your chat application
    "RoomName": "lobby",            // The default room/channel name
    "FunnyTexts": {                 // Collections of random text variations
      // ... text collections ...
    }
  }
}
```

### Funny Text Collections

#### Welcome Messages
Displayed when users first arrive at the chat. Use `{0}` as placeholder for project name.
```json
"WelcomeMessages": [
  "Welcome to {0}! Where conversations happen and typos are celebrated!",
  "Greetings, brave soul! You've entered {0}, abandon all productivity!",
  // ... more variations
]
```

#### Join Button Texts
Random text for the join button:
```json
"JoinButtonTexts": [
  "Join Chat",
  "Enter the Conversation",
  "Jump In!",
  // ... more variations
]
```

#### Username Placeholders
Placeholder text for the username input field:
```json
"UsernamePlaceholders": [
  "Enter your username",
  "What should we call you?",
  "Pick a cool name",
  // ... more variations
]
```

#### Message Placeholders
Placeholder text for the message input field:
```json
"MessagePlaceholders": [
  "Type a message...",
  "Say something nice...",
  "Share your thoughts...",
  // ... more variations
]
```

#### Connection Statuses
Different ways to show connection state:
```json
"ConnectionStatuses": {
  "Connected": [
    "Connected",
    "Online",
    "Plugged In",
    // ... more variations
  ],
  "Disconnected": [
    "Disconnected",
    "Offline",
    "Connection Lost",
    // ... more variations
  ]
}
```

#### System Messages
Messages shown when users join or leave. Use `{0}` for username:
```json
"SystemMessages": {
  "UserJoined": [
    "{0} joined the chat",
    "{0} has entered the building",
    "Wild {0} appeared!",
    // ... more variations
  ],
  "UserLeft": [
    "{0} left the chat",
    "{0} disappeared into the void",
    "{0} went to get snacks",
    // ... more variations
  ]
}
```

#### Typing Indicators
Messages shown when users are typing:
```json
"TypingIndicators": {
  "Single": [
    "{0} is typing..",
    "{0} is crafting a message..",
    // ... more variations
  ],
  "Double": [
    "{0} and {1} are typing..",
    "{0} and {1} are racing to type..",
    // ... more variations
  ],
  "Multiple": [
    "{0} and {1} others are typing..",
    "It's a typing party with {0} and {1} others..",
    // ... more variations
  ]
}
```

#### Other UI Elements
```json
"OnlineUsersHeader": [
  "Online Users ({0})",
  "Active Chatters ({0})",
  "Current Squad ({0})",
  // ... more variations
],
"RoomHeaders": [
  "# {0}",
  "#{0} - Where magic happens",
  "Welcome to #{0}",
  // ... more variations
]
```

## How It Works

1. **Random Selection**: Each UI element randomly selects from its configured text variations when rendered
2. **Fallback Values**: If a configuration is missing, sensible defaults are used
3. **Environment-Specific**: Use `appsettings.Development.json` to override values for development

## Example Development Configuration

```json
{
  "ChatSettings": {
    "ProjectName": "DevChat",
    "RoomName": "dev-lounge",
    "FunnyTexts": {
      "WelcomeMessages": [
        "Welcome to {0} DEV MODE! Bugs are features here!",
        "{0} Development Edition - Where console.log() is your best friend"
      ],
      "JoinButtonTexts": [
        "Deploy to Chat",
        "git push origin chat"
      ]
    }
  }
}
```

## Adding New Variations

Simply add more strings to any array in the configuration. The system will automatically include them in the random selection.

## Technical Implementation

The `ChatConfigService` class handles:
- Loading configuration values
- Random selection of text variations
- Formatting placeholders with actual values
- Providing fallback defaults

The service is injected into the Chat component and used to populate UI text elements dynamically.