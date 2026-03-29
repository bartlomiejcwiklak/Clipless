<img width="500"  alt="logo" src="https://github.com/user-attachments/assets/54e198f4-24f9-4252-b09b-1665420d8321" />

## An easy to use clip recorder, editor and organizer for gaming

<img width="2560" height="720" alt="screens" src="https://github.com/user-attachments/assets/fb9373c6-6c95-4046-ab74-c296f7591271" />

## Features
### Background Recording & Capture
- **Continuous Buffer Recording**: Uses FFmpeg to continuously record a rolling background buffer (up to 30 minutes) so you never miss a moment.
- **Global Save Hotkeys**: Save the buffered clip instantly using customizable system-wide hotkeys (default Ctrl+Shift+S).
- **Smart Game Detection**: Automatically detects the active foreground process/game to intelligently name and organize saved clips into respective folders.
- **Capture Customization**: Choose your target capture monitor, resolution (Native, 1080p, or 720p), and framerate (30 or 60 FPS).
- **Hardware Acceleration**: Support for both GPU (NVENC) and CPU rendering engines.
- **Advanced Audio Setup**: Select specific audio input (microphone) and output (system sound) devices to mix into your recordings.
- **Webcam Support**: Optional camera overlay feature to include your facecam in the recording.

### Built-In Playback & Editing
- **Integrated Media Player**: Smooth video playback powered by LibVLC, complete with volume control and variable playback speed.
- **Video Trimming**: Trim the start and end of your clips using a dedicated timeline canvas with drag handles.
- **Audio Waveforms**: Automatically generates visual audio waveforms for your clips to help you find exact moments to cut.
- **Precision Scrubbing**: Frame-by-frame navigation using keyboard shortcuts (, and .) for accurate edits.
- **Optimized Exports**: Export your trimmed clips with target file size optimization (dynamically calculates bitrate) and optional audio normalization.

### Clip Management & Organization
- **Custom Tagging**: Create color-coded custom tags to filter and organize your library beyond just game folders.
- **Favorites System**: Star your best clips to keep them safe and easily accessible in a dedicated Favorites view.
- **Hover Previews**: Hover your mouse over video thumbnails to scrub through generated preview frames of the clip.
- **Automated Storage Management**: Optional auto-delete feature that cleans up non-favorited clips older than a customizable number of weeks.
- **File Operations**: Built-in sorting (by date, size, name) and standard file operations (rename, delete, copy, cut, paste).

### System Integration
- **System Tray Operation**: Runs quietly in the background and minimizes to the system tray so it stays out of your way.
- **Run on Boot**: Optional setting to launch the app automatically with Windows.
- **Localization**: Built-in support for multiple UI languages via resource dictionaries.

## Credits
Made by ohhbaro
Tested extensively by Liify

## Acknowledgements 
This project uses:
- LibVLCSharp by videolan
- FFmpeg by ffmpeg
- WPF-UI by lepoco
