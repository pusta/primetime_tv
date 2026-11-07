# PrimeTime for Apple platforms

This folder will hold the native PrimeTime app for **iOS, iPadOS and tvOS**, written in Swift
(SwiftUI with AVKit for playback). Nothing is built here yet.

The plan:

- Sign in to a PrimeTime server and remember it.
- Show the channel guide: what's on now and next on each channel.
- Tune to a channel and join the program in progress, the way live TV works.
- Channel up and down on the Apple TV remote.

The app will talk only to the PrimeTime server's API; it won't need its own Jellyfin credentials.
That API doesn't exist yet beyond the admin endpoints, so the app will start once channels and
schedules are in place on the server.

The app is meant for the App Store, which is one reason the project is MIT-licensed.
