# midi-filter

This program is mostly coded with Anthropic Claude Sonnet 4.6!

---

<img width="468" height="577" alt="{0BF11715-DE94-4AF1-B902-EC95B144BD77}" src="https://github.com/user-attachments/assets/558362fa-fe44-46d1-9bad-d46bb295ff8b" />


---

This is a simple C# application that filters MIDI pedal controller messages from midi keyboards in real time.
The program listens to incoming MIDI data and detects or removes common pedal inputs.

The filter is <ins>currently</ins> controlling only pedal controllers:
- Sustain (CC 64)
- Expression (CC 11)
- Sostenuto (CC 66)
- Soft Pedal (CC 67)


This is useful when certain applications sends data to other applications, in my case between Synthesia and Pianoteq, and affect the way you play.

See my personal routing here:

<img width="696" height="708" alt="{B7D15215-1901-4D88-9F04-62F1B6DDB316}" src="https://github.com/user-attachments/assets/4e46ae2d-4ade-4373-9ac0-15901c466cb4" />




---
## Features

- You are able to select Input and Output from all available sources to route to desired destination. 
- The filter automatically activates once an Input and Output was set in the previous session! Everything you set up will be saved for the next session so you can start playing right away!
<img width="441" height="230" alt="{A5A2ED73-2942-48F8-96FA-06F893397801}" src="https://github.com/user-attachments/assets/40799a53-7444-41e9-ae87-274c94739e22" />

- Easily disable or enable the idividual filters by checking the boxes beside them
<img width="188" height="147" alt="{141ADEAA-16F6-47FA-B4E8-6093A165FCEC}" src="https://github.com/user-attachments/assets/6a7f27a6-db93-4b1c-9058-298f3b8cd9ff" />

- You'll be able to see when data got filtered out in the activity log below.
<img width="440" height="169" alt="{9D58AB50-1D12-4921-A5B2-2B1324E19E16}" src="https://github.com/user-attachments/assets/0aca00ee-8d8e-4dd7-bfd3-42f641317a7b" />

- Easily start and stop the filter when needed. You can also refresh the device list whenever you connect something new!
<img width="442" height="143" alt="image" src="https://github.com/user-attachments/assets/b32e890e-9e06-4ef2-be04-8f8f20325859" />


---
## Installation

Simply download the MidiFilter.exe file and start it.
I recommend putting it in a separate folder since it creates a settings.cfg file once an Input and Output device are set.

You can also build the project on your own. For this .NET 8 is required.
I provid you a small script which automatically builds it if you want to. It's called build.cmd. You need to place it in the root folder where the folder "files" is located.

Official Microsoft Link to the download page:
https://dotnet.microsoft.com/en-us/download/dotnet/8.0

---
## Future Endeavours

[] additional Filter for Volume Controller
[] adding multiple custom filter to handle all Controllers needed
