
We requested someone to write this project for us to use in our enterprise application and want a full evaluation of it now.

Our requirements:
- At least once guarantee for messages is a must have
- Async API documentation of the topology with EventCatalog extras
- Should be compatible with any message schema other services might use.
  - they might not use this project in their services
  - different messages might have different schema formats and message headers formats.
- Default to ClaudEvents and standard RabbitMQ headers
- transactional integrity of events together with database saves via EfCore
- automatic retry of failed messages up to a configured amount. After that manual retry.
- Compatible with multiple different DbContexts in a project
- Sending of local messages within a service from one module to another (with different DbContexts)
- good observabillity (tracing, metrics, logs) that makes it easy to spot and debug problems
- Stable and bug free
- Easy to maintain
- No security issues
- Easy to use with intuitive configuration/behaviour

If there are weaknesses in the implementation we need to know them now before we pay the money for it.
Everthing we notice afterwards we will cost us a lot of money.


-----------------------------------


Are there any additional things to be aware of outside of our requirements? Maybe we haven't thought of something important.