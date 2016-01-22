// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

'use strict';

var mqtt = require('mqtt');

/**
 * @class           module:azure-iot-mqtt-base.Mqtt
 * @classdesc       Base MQTT transport implementation used by higher-level IoT Hub libraries. 
 *                  You generally want to use these higher-level objects (such as [azure-iot-device-mqtt.Mqtt]{@link module:azure-iot-device-mqtt.Mqtt})
 *                  rather than this one.
 * 
 * @param {Object}  config      The configuration object derived from the connection string.
 */
/*
 Codes_SRS_NODE_HTTP_12_001: MqttTransport shall accept the following argument:
    config [
        host: host address
        deviceID: device name
        sharedAccessSignature: SAS token  created for IoTHub
        gatewayHostName: gateway host name]
 Codes_SRS_NODE_HTTP_12_002: [MqttTransport shall throw ReferenceError “Invalid transport configuration” error if either of the configuration field is falsy
 Codes_SRS_NODE_HTTP_12_003: [MqttTransport shall create a configuration structure for underlying MQTT.JS library and store it to a member variable
 Codes_SRS_NODE_HTTP_12_004: [MqttTransport shall return an instance itself
*/
function Mqtt(config) {
  if ((!config) ||
    (!config.host) ||
    (!config.deviceId) ||
    (!config.sharedAccessSignature) ||
    (!config.gatewayHostName))
    throw new ReferenceError('Invalid transport configuration');

  this._gatewayHostName = config.gatewayHostName;
  this._topic_publish = "devices/" + config.deviceId + "/messages/events";
  this._topic_subscribe = "devices/" + config.deviceId + "/messages/devicebound";
  this._options =
  {
    cmd: 'connect',
    protocolId: 'MQTT',
    protocolVersion: 4,
    clean: false,
    clientId: config.deviceId,
    username: config.host + '/' + config.deviceId,
    password: new Buffer(config.sharedAccessSignature),
    rejectUnauthorized: false,
  };
  return this;
}

/**
 * @method            module:azure-iot-mqtt-base.Mqtt#connect
 * @description       Establishes a secure connection to the IoT Hub instance using the MQTT protocol.
 * 
 * @param {Function}  done  Callback that shall be called when the connection is established.
 */
/* SRS_NODE_HTTP_12_005: The CONNECT method shall call connect on MQTT.JS  library and return a promise with the result */
Mqtt.prototype.connect = function (done) {
  this.client = mqtt.connect(this._gatewayHostName, this._options);
  if (done) {
    var errCallback = function (error) {
      done(error);
    };
    this.client.on('error', errCallback);

    this.client.on('connect', function () {
      this.client.removeListener('error', errCallback);
      done();
    }.bind(this));
  }
};

/**
 * @method            module:azure-iot-mqtt-base.Mqtt#disconnect
 * @description       Terminates the connection to the IoT Hub instance.
 * 
 * @param {Function}  done      Callback that shall be called when the connection is terminated.
 */
Mqtt.prototype.disconnect = function (done) {
    this.client.removeAllListeners();
    // The first parameter decides whether the connection should be forcibly closed without waiting for all sent messages to be ACKed.
    // This should be a transport-specific parameter.
    /* Codes_SRS_NODE_HTTP_16_001: [The disconnect method shall call the done callback when the connection to the server has been closed.] */
    this.client.end(false, done);
};

/**
 * @method            module:azure-iot-mqtt-base.Mqtt#publish
 * @description       Publishes a message to the IoT Hub instance using the MQTT protocol.
 * 
 * @param {Object}    message   Message to send to the IoT Hub instance.
 * @param {Function}  done      Callback that shall be called when the connection is established.
 */
/* Codes_SRS_NODE_HTTP_12_006: The PUBLISH method shall throw ReferenceError “Invalid message” if the message is falsy */
/* Codes_SRS_NODE_HTTP_12_007: The PUBLISH method shall call publish on MQTT.JS library with the given message */
Mqtt.prototype.publish = function (message, done) {
  if (!message)
    throw new ReferenceError('Invalid message');

  if (done) {
    var errCallback = function (error) {
      done(error);
    };
    this.client.on('error', errCallback);
  }
  this.client.publish(this._topic_publish, message.data.toString(), function () {
    if (done) {
      this.client.removeListener('error', errCallback);
      done();
    }
  });
};

/**
 * @method            module:azure-iot-mqtt-base.Mqtt#subscribe
 * @description       Subscribes to messages coming from the IoT Hub instance.
 * 
 * @param {Function}  done  Callback that shall be called when the subscription is successful or if an error happens.
 */
/* Codes_SRS_NODE_HTTP_12_008: The SUBSCRIBE method shall call subscribe on MQTT.JS library with the given message and with the hardcoded topic path */
Mqtt.prototype.subscribe = function (done) {
  if (done) {
    var errCallback = function (error) {
      done(error);
    };
    this.client.on('error', errCallback);
  }
  this.client.subscribe(this._topic_subscribe, function () {
    if (done) {
      this.client.removeListener('error', errCallback);
      done();
    }
  });
};

/**
 * @method            module:azure-iot-mqtt-base.Mqtt#receive
 * @description       Receives messages from the IoT Hub instance.
 * 
 * @param {Function}  done  Callback that shall be called when a message arrives or if an error happens.
 */
/* Codes_SRS_NODE_HTTP_12_010: The RECEIVE method shall implement the MQTT.JS library callback event and calls back to the caller with the given callback */
Mqtt.prototype.receive = function (done) {
  if (done) {
    var errCallback = function (error) {
      done(error);
    };
    this.client.on('error', errCallback);
  }
  this.client.on('message', function (topic, msg) {
    this.client.removeListener('error', errCallback);
    if (done) done(topic, msg);
  }.bind(this));
};

module.exports = Mqtt;