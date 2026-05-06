The main goal of the examples is to cover as much ratatoskr functionallity as possible. For example:
* Inbox/outbox with success, failure, retry and poison. 
* Direct publish 
* Direct consume (no inbox) with success, failure, retry and dlq. 
* Multiple handlers for the same message
* both rabbitmq and efcore transport

The dashboard should be able to:
* start scenarios
* show the result of the scenario including details
* show the status of the system including transport details. 