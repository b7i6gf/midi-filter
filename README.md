# midi-filter

This program is mostly coded with Anthropic Claude Sonnet 4.6 and Opus 4.8!

---

<img width="462" height="688" alt="{D0E875F3-0E7A-42EF-9E16-A1BB52DED0A3}" src="https://github.com/user-attachments/assets/950f9fb3-51cd-40f0-96a5-8643312ab962" />




---

This is a simple C# application that filters MIDI pedal controller messages from midi keyboards in real time.
The program listens to incoming MIDI data and detects or removes common pedal inputs.

The standard configuration of the filter includes the following controllers:
- Volume (CC 7)
- Sustain (CC 64)
- Expression (CC 11)
- Sostenuto (CC 66)
- Soft Pedal (CC 67)

It is now possible, since Version 1.4.0, to add custom controllers and notes to the filter!
<img width="454" height="533" alt="{DB773B86-451F-4E36-87DF-C7ADB2CD5FD4}" src="https://github.com/user-attachments/assets/a236b6eb-b9dc-4221-9bdd-fc4e318daa1f" />


This is especially useful when certain applications sends data to other applications, in my case between Synthesia and Pianoteq, and affect the way you play.

See my personal routing here:

<img width="696" height="708" alt="{B7D15215-1901-4D88-9F04-62F1B6DDB316}" src="https://github.com/user-attachments/assets/4e46ae2d-4ade-4373-9ac0-15901c466cb4" />




---
## Features

- You are able to select Input and Output from all available sources to route to desired destination. 
- The filter automatically activates once an Input and Output was set in the previous session! Everything you set up will be saved for the next session so you can start playing right away!
<img width="441" height="230" alt="{A5A2ED73-2942-48F8-96FA-06F893397801}" src="https://github.com/user-attachments/assets/40799a53-7444-41e9-ae87-274c94739e22" />

---

- Easily disable or enable the individual filters by checking the boxes beside them. Or all at once using the btton above.
<img width="437" height="195" alt="{FEBBAF01-E175-4600-96FF-AC91754DC333}" src="https://github.com/user-attachments/assets/8277a28c-085a-4308-8088-c23262666c83" />

---

- You'll be able to see when data got filtered out in the activity log below.
<img width="440" height="169" alt="{9D58AB50-1D12-4921-A5B2-2B1324E19E16}" src="https://github.com/user-attachments/assets/0aca00ee-8d8e-4dd7-bfd3-42f641317a7b" />

---

- Easily start and stop the filter when needed. You can also refresh the device list whenever you connect something new or restart the app when something is off!
<img width="446" height="132" alt="{573472A2-A87D-4C61-94E5-4C8F535A685A}" src="https://github.com/user-attachments/assets/efeb286a-3024-4348-92b8-908df7a79830" />

---

- Add custom filters, rename them or simply delete them again.
<img width="437" height="252" alt="image" src="https://github.com/user-attachments/assets/07e25876-337e-4493-b521-86b565d14e75" />


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

[] adding multiple custom filter to handle all Controllers needed
