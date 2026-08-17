#include <array>
#include <cstdint>
#include <fstream>
#include <memory>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include <Eigen/Geometry>
#include <fcl/fcl.h>
#include <rclcpp/rclcpp.hpp>
#include <sensor_msgs/msg/joint_state.hpp>
#include <urdf/model.h>

#include "printer_initialization_msgs/msg/collision_candidate.hpp"
#include "printer_initialization_msgs/srv/evaluate_collision.hpp"

namespace
{
#pragma pack(push, 1)
struct StlTriangle
{
  float normal[3];
  float first[3];
  float second[3];
  float third[3];
  std::uint16_t attributes;
};
#pragma pack(pop)
static_assert(sizeof(StlTriangle) == 50);

using Mesh = fcl::BVHModel<fcl::OBBRSSd>;

std::shared_ptr<Mesh> load_binary_stl(const std::string & path)
{
  const std::string local_path = path.rfind("file://", 0) == 0 ? path.substr(7) : path;
  std::ifstream input(local_path, std::ios::binary);
  if (!input) {
    throw std::runtime_error("Cannot open collision mesh: " + path);
  }

  std::array<char, 80> header{};
  std::uint32_t triangle_count{};
  input.read(header.data(), header.size());
  input.read(reinterpret_cast<char *>(&triangle_count), sizeof(triangle_count));
  if (!input) {
    throw std::runtime_error("Invalid binary STL header: " + path);
  }

  auto mesh = std::make_shared<Mesh>();
  mesh->beginModel(triangle_count);
  for (std::uint32_t index = 0; index < triangle_count; ++index) {
    StlTriangle triangle{};
    input.read(reinterpret_cast<char *>(&triangle), sizeof(triangle));
    if (!input) {
      throw std::runtime_error("Unexpected end of binary STL: " + path);
    }
    mesh->addTriangle(
      {triangle.first[0], triangle.first[1], triangle.first[2]},
      {triangle.second[0], triangle.second[1], triangle.second[2]},
      {triangle.third[0], triangle.third[1], triangle.third[2]});
  }
  mesh->endModel();
  return mesh;
}

Eigen::Isometry3d pose_to_transform(const urdf::Pose & pose)
{
  Eigen::Isometry3d transform = Eigen::Isometry3d::Identity();
  transform.translation() = Eigen::Vector3d(pose.position.x, pose.position.y, pose.position.z);
  transform.linear() = Eigen::Quaterniond(
    pose.rotation.w, pose.rotation.x, pose.rotation.y, pose.rotation.z).toRotationMatrix();
  return transform;
}

struct CollisionShape
{
  std::string link_name;
  Eigen::Isometry3d link_to_collision;
  std::shared_ptr<Mesh> mesh;
};

struct CollisionPair
{
  std::string first;
  std::string second;
};
}  // namespace

class FclCollisionDetectorNode : public rclcpp::Node
{
public:
  FclCollisionDetectorNode()
  : Node("fcl_collision_detector_node")
  {
    const auto robot_description_file = declare_parameter<std::string>("robot_description_file", "");
    active_joint_ = declare_parameter<std::string>("active_joint", "");
    direction_ = declare_parameter<int>("direction", 0);
    if (robot_description_file.empty()) {
      throw std::runtime_error("The 'robot_description_file' parameter is required.");
    }
    if (!model_.initFile(robot_description_file)) {
      throw std::runtime_error("Unable to parse URDF: " + robot_description_file);
    }
    load_collision_shapes();

    candidate_publisher_ = create_publisher<printer_initialization_msgs::msg::CollisionCandidate>(
      "/initialization/collision_candidate", 10);
    joint_state_subscription_ = create_subscription<sensor_msgs::msg::JointState>(
      "/joint_states", 10,
      std::bind(&FclCollisionDetectorNode::on_joint_state, this, std::placeholders::_1));
    evaluation_service_ = create_service<printer_initialization_msgs::srv::EvaluateCollision>(
      "/initialization/evaluate_collision",
      std::bind(&FclCollisionDetectorNode::on_evaluate_collision, this,
        std::placeholders::_1, std::placeholders::_2));
    RCLCPP_INFO(get_logger(), "Loaded %zu collision mesh(es) from %s.",
      collision_shapes_.size(), robot_description_file.c_str());
  }

private:
  void load_collision_shapes()
  {
    for (const auto & entry : model_.links_) {
      const auto & link = entry.second;
      for (const auto & collision : link->collision_array) {
        const auto mesh = std::dynamic_pointer_cast<urdf::Mesh>(collision->geometry);
        if (!mesh) {
          RCLCPP_WARN(get_logger(), "Skipping non-mesh collision geometry on %s.", link->name.c_str());
          continue;
        }
        if (mesh->scale.x != 1.0 || mesh->scale.y != 1.0 || mesh->scale.z != 1.0) {
          throw std::runtime_error("Only collision mesh scale=1 is supported: " + link->name);
        }
        collision_shapes_.push_back({link->name, pose_to_transform(collision->origin), load_binary_stl(mesh->filename)});
      }
    }
    if (collision_shapes_.empty()) {
      throw std::runtime_error("The URDF contains no mesh collision shapes.");
    }
  }

  Eigen::Isometry3d joint_transform(
    const urdf::JointSharedPtr & joint,
    const std::unordered_map<std::string, double> & positions) const
  {
    Eigen::Isometry3d transform = pose_to_transform(joint->parent_to_joint_origin_transform);
    if (joint->type == urdf::Joint::PRISMATIC) {
      const auto found = positions.find(joint->name);
      const double distance = found == positions.end() ? 0.0 : found->second;
      transform.translate(Eigen::Vector3d(joint->axis.x, joint->axis.y, joint->axis.z) * distance);
    }
    return transform;
  }

  void calculate_link_transforms(
    const urdf::LinkConstSharedPtr & link, const Eigen::Isometry3d & link_to_world,
    const std::unordered_map<std::string, double> & positions,
    std::unordered_map<std::string, Eigen::Isometry3d> & transforms) const
  {
    transforms.emplace(link->name, link_to_world);
    for (std::size_t index = 0; index < link->child_links.size(); ++index) {
      calculate_link_transforms(
        link->child_links[index],
        link_to_world * joint_transform(link->child_joints[index], positions),
        positions, transforms);
    }
  }

  std::vector<CollisionPair> evaluate(
    const std::unordered_map<std::string, double> & positions) const
  {
    std::unordered_map<std::string, Eigen::Isometry3d> transforms;
    calculate_link_transforms(model_.getRoot(), Eigen::Isometry3d::Identity(), positions, transforms);

    fcl::CollisionRequestd request;
    request.enable_contact = false;
    std::vector<CollisionPair> collisions;
    for (std::size_t first = 0; first < collision_shapes_.size(); ++first) {
      const auto first_transform = transforms.at(collision_shapes_[first].link_name) *
        collision_shapes_[first].link_to_collision;
      fcl::CollisionObjectd first_object(collision_shapes_[first].mesh, first_transform);
      for (std::size_t second = first + 1; second < collision_shapes_.size(); ++second) {
        if (collision_shapes_[first].link_name == collision_shapes_[second].link_name) {
          continue;
        }
        const auto second_transform = transforms.at(collision_shapes_[second].link_name) *
          collision_shapes_[second].link_to_collision;
        fcl::CollisionObjectd second_object(collision_shapes_[second].mesh, second_transform);
        fcl::CollisionResultd result;
        fcl::collide(&first_object, &second_object, request, result);
        if (result.isCollision()) {
          collisions.push_back({collision_shapes_[first].link_name, collision_shapes_[second].link_name});
        }
      }
    }
    return collisions;
  }

  void on_joint_state(const sensor_msgs::msg::JointState::SharedPtr message)
  {
    const auto count = std::min(message->name.size(), message->position.size());
    for (std::size_t index = 0; index < count; ++index) {
      joint_positions_[message->name[index]] = message->position[index];
    }
    for (const auto & pair : evaluate(joint_positions_)) {
      printer_initialization_msgs::msg::CollisionCandidate candidate;
      candidate.active_joint = active_joint_;
      candidate.direction = direction_;
      candidate.link_a = pair.first;
      candidate.link_b = pair.second;
      candidate_publisher_->publish(candidate);
    }
  }

  void on_evaluate_collision(
    const std::shared_ptr<printer_initialization_msgs::srv::EvaluateCollision::Request> request,
    std::shared_ptr<printer_initialization_msgs::srv::EvaluateCollision::Response> response)
  {
    std::unordered_map<std::string, double> requested_positions;
    const auto count = std::min(request->joint_names.size(), request->joint_positions.size());
    for (std::size_t index = 0; index < count; ++index) {
      requested_positions[request->joint_names[index]] = request->joint_positions[index];
    }
    const auto collisions = evaluate(requested_positions);
    for (const auto & pair : collisions) {
      response->link_a.push_back(pair.first);
      response->link_b.push_back(pair.second);
    }
    if (!collisions.empty()) {
      RCLCPP_INFO(get_logger(),
        "EVALUATE_COLLISION active_joint=%s direction=%d collision_count=%zu first_pair=%s<->%s",
        active_joint_.c_str(), direction_, collisions.size(),
        collisions.front().first.c_str(), collisions.front().second.c_str());
    }
  }

  urdf::Model model_;
  std::string active_joint_;
  int direction_{};
  std::unordered_map<std::string, double> joint_positions_;
  std::vector<CollisionShape> collision_shapes_;
  rclcpp::Publisher<printer_initialization_msgs::msg::CollisionCandidate>::SharedPtr candidate_publisher_;
  rclcpp::Subscription<sensor_msgs::msg::JointState>::SharedPtr joint_state_subscription_;
  rclcpp::Service<printer_initialization_msgs::srv::EvaluateCollision>::SharedPtr evaluation_service_;
};

int main(int argc, char ** argv)
{
  rclcpp::init(argc, argv);
  rclcpp::spin(std::make_shared<FclCollisionDetectorNode>());
  rclcpp::shutdown();
  return 0;
}
