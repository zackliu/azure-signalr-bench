from Util.BenchmarkConfigurationStep import *
from Util import TemplateSetter, ConfigSaver, CommonStep


class SendToClient:
    def __init__(self, sending_config, scenario_config, connection_config, statistics_config, constant_config):
        self.sending_config = sending_config
        self.scenario_config = scenario_config
        self.statistics_config = statistics_config
        self.connection_config = connection_config
        self.constant_config = constant_config

    def generate_config(self):
        pre_sending = CommonStep.pre_sending_steps(self.scenario_config.type, self.connection_config,
                                                   self.statistics_config, self.scenario_config)
        pre_sending += [register_callback_record_latency(self.scenario_config.type)]

        pre_sending += [collect_connection_id(self.scenario_config.type)]

        post_sending = CommonStep.post_sending_steps(self.scenario_config.type)

        remainder_begin = 0
        remainder_end_dx = self.scenario_config.step

        sending = []
        for epoch in range(0, self.scenario_config.step_length):
            remainder_end = self.scenario_config.base_step + epoch * remainder_end_dx

            if remainder_end - remainder_begin > self.scenario_config.connections:
                break

            # conditional stop and reconnect
            if epoch > 0:
                sending += CommonStep.conditional_stop_and_reconnect_steps(sending, self.scenario_config, self.constant_config,
                                            self.connection_config)

            sending += [
                send_to_client(self.scenario_config.type, self.scenario_config.connections,
                               self.sending_config.duration, self.sending_config.interval, remainder_begin,
                               remainder_end, self.scenario_config.connections, self.sending_config.message_size),
                wait(self.scenario_config.type, self.constant_config.wait_time)
            ]

        pipeline = pre_sending + sending + post_sending

        config = TemplateSetter.set_config(self.constant_config.module, [self.scenario_config.type], pipeline)

        ConfigSaver.save_yaml(config, self.constant_config.config_save_path)
