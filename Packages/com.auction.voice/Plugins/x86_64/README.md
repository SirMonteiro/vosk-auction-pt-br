Native libraries go here, one folder per platform:

    Plugins/x86_64/vosk.dll       (Windows standalone)
    Plugins/x86_64/libvosk.so     (Linux standalone)
    Plugins/x86_64/libvosk.dylib  (macOS standalone)

They are NOT committed: they are large platform binaries with their own release cadence.
Take them from a Vosk release you trust, or from the `vosk` Python wheel (the shared library
inside it is the same library this package P/Invokes).

Unity copies this folder next to the player automatically, which is why VoskPInvoke uses the
bare names "vosk" / "libvosk".
