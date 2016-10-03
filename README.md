# MongoTools
A simple set of tools written for administration of MongoDB servers. All the tools available via this project works via CLI and were tested on Mono aswell, so you can run them on your unix environment if you feel like

This set of tools takes advantage of Parallel processing to perform each operation in it's own thread. Since MongoDB uses, for some operations, a "Collection-Level" lock,
by using one thread per collection processing, I can take full advantage of both the database performance and the network speed.

Read the Wiki for the parameters and examples of each  tool.

Tools Available
======================
**[Export]** : Exports data from your MongoDB collections either as "CSV" (with a custom delimiter) or as "JSON". 

**[Copy]**  : Migrates data (and indexes) from one database to another (or from a server to another).

[Export]:https://github.com/MarcelloLins/MongoTools/wiki/Tool-:-Export
[Copy]:https://github.com/MarcelloLins/MongoTools/wiki/Tool:-Migration