#include <algorithm>
#include <string>
#include <utility>
#include <vector>

#include <rclcpp/rclcpp.hpp>
#include <yaml-cpp/yaml.h>

#include "printer_initialization_msgs/msg/collision_candidate.hpp"
#include "printer_initialization_msgs/msg/collision_decision.hpp"
#include "printer_initialization_msgs/srv/evaluate_collision_policy.hpp"
#include "printer_initialization_msgs/srv/set_gantry_base_exception.hpp"

namespace
{
struct CollisionRule
{
  std::string id;
  std::string active_joint;
  int direction{};
  std::vector<std::pair<std::string, std::string>> ignored_pairs;
};

std::pair<std::string, std::string> normalized_pair(std::string first, std::string second)
{
  if (second < first) {
    std::swap(first, second);
  }
  return {first, second};
}
}  // namespace

class CollisionMonitorNode : public rclcpp::Node
{
public:
  CollisionMonitorNode()
  : Node("collision_monitor_node")
  {
    const auto policy_file = declare_parameter<std::string>("policy_file", "");
    if (policy_file.empty()) {
      throw std::runtime_error("The 'policy_file' parameter is required.");
    }
    load_rules(policy_file);

    decision_publisher_ = create_publisher<printer_initialization_msgs::msg::CollisionDecision>(
      "/initialization/collision_decision", 10);
    candidate_subscription_ = create_subscription<printer_initialization_msgs::msg::CollisionCandidate>(
      "/initialization/collision_candidate", 10,
      std::bind(&CollisionMonitorNode::on_candidate, this, std::placeholders::_1));
    policy_service_ = create_service<printer_initialization_msgs::srv::EvaluateCollisionPolicy>(
      "/initialization/evaluate_collision_policy",
        std::bind(&CollisionMonitorNode::on_evaluate_policy, this,
        std::placeholders::_1, std::placeholders::_2));
    exception_service_ = create_service<printer_initialization_msgs::srv::SetGantryBaseException>(
      "/initialization/set_gantry_base_exception",
      std::bind(&CollisionMonitorNode::on_set_exception, this,
        std::placeholders::_1, std::placeholders::_2));
    RCLCPP_INFO(get_logger(), "Loaded %zu initialization collision policy rule(s).", rules_.size());
  }

private:
  void load_rules(const std::string & policy_file)
  {
    const auto root = YAML::LoadFile(policy_file);
    if (!root["rules"]) {
      return;
    }
    for (const auto & rule_node : root["rules"]) {
      CollisionRule rule;
      rule.id = rule_node["id"].as<std::string>();
      rule.active_joint = rule_node["active_joint"].as<std::string>();
      const auto direction = rule_node["direction"].as<std::string>();
      rule.direction = direction == "negative" ? -1 : 1;
      for (const auto & pair : rule_node["ignore_pairs"]) {
        rule.ignored_pairs.push_back(normalized_pair(pair[0].as<std::string>(), pair[1].as<std::string>()));
      }
      rules_.push_back(std::move(rule));
    }
  }

  void on_candidate(const printer_initialization_msgs::msg::CollisionCandidate::SharedPtr candidate)
  {
    const auto decision = evaluate(candidate->active_joint, candidate->direction,
      candidate->link_a, candidate->link_b, 0.0, true);
    decision_publisher_->publish(decision);
  }

  printer_initialization_msgs::msg::CollisionDecision evaluate(
    const std::string & active_joint, int direction,
    const std::string & link_a, const std::string & link_b,
    double z_position, bool calibration_phase) const
  {
    printer_initialization_msgs::msg::CollisionDecision decision;
    decision.active_joint = active_joint;
    decision.direction = direction;
    decision.link_a = link_a;
    decision.link_b = link_b;
    decision.ignored = false;
    const auto pair = normalized_pair(link_a, link_b);
    if (!calibration_phase && dynamic_exception_valid_ && active_joint == "base_to_z_gantry" &&
      pair == normalized_pair("base_link", "z_gantry_link") &&
      z_position >= dynamic_exception_z_min_ && z_position <= dynamic_exception_z_max_)
    {
      decision.ignored = true;
      decision.rule_id = "ignore_measured_gantry_base_contact_range";
      return decision;
    }
    for (const auto & rule : rules_) {
      const bool direction_matches = (direction < 0 && rule.direction < 0) ||
        (direction > 0 && rule.direction > 0);
      if (calibration_phase && active_joint == rule.active_joint && direction_matches &&
        std::find(rule.ignored_pairs.begin(), rule.ignored_pairs.end(), pair) != rule.ignored_pairs.end())
      {
        decision.ignored = true;
        decision.rule_id = rule.id;
        break;
      }
    }
    return decision;
  }

  void on_evaluate_policy(
    const std::shared_ptr<printer_initialization_msgs::srv::EvaluateCollisionPolicy::Request> request,
    std::shared_ptr<printer_initialization_msgs::srv::EvaluateCollisionPolicy::Response> response)
  {
    const auto decision = evaluate(request->active_joint, request->direction,
      request->link_a, request->link_b, request->z_position, request->calibration_phase);
    response->ignored = decision.ignored;
    response->rule_id = decision.rule_id;
  }

  void on_set_exception(
    const std::shared_ptr<printer_initialization_msgs::srv::SetGantryBaseException::Request> request,
    std::shared_ptr<printer_initialization_msgs::srv::SetGantryBaseException::Response> response)
  {
    dynamic_exception_z_min_ = std::min(request->z_min, request->z_max);
    dynamic_exception_z_max_ = std::max(request->z_min, request->z_max);
    dynamic_exception_valid_ = true;
    response->accepted = true;
    response->rule_id = "ignore_measured_gantry_base_contact_range";
    RCLCPP_INFO(get_logger(), "Measured gantry-base exception z=[%.3f, %.3f]",
      dynamic_exception_z_min_, dynamic_exception_z_max_);
  }

  std::vector<CollisionRule> rules_;
  rclcpp::Publisher<printer_initialization_msgs::msg::CollisionDecision>::SharedPtr decision_publisher_;
  rclcpp::Subscription<printer_initialization_msgs::msg::CollisionCandidate>::SharedPtr candidate_subscription_;
  rclcpp::Service<printer_initialization_msgs::srv::EvaluateCollisionPolicy>::SharedPtr policy_service_;
  rclcpp::Service<printer_initialization_msgs::srv::SetGantryBaseException>::SharedPtr exception_service_;
  bool dynamic_exception_valid_{false};
  double dynamic_exception_z_min_{0.0};
  double dynamic_exception_z_max_{0.0};
};

int main(int argc, char ** argv)
{
  rclcpp::init(argc, argv);
  rclcpp::spin(std::make_shared<CollisionMonitorNode>());
  rclcpp::shutdown();
  return 0;
}
