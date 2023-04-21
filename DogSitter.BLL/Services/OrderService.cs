﻿using AutoMapper;
using DogSitter.BLL.Exeptions;
using DogSitter.BLL.Helpers;
using DogSitter.BLL.Models;
using DogSitter.BLL.Services.Interfaces;
using DogSitter.DAL.Entity;
using DogSitter.DAL.Enums;
using DogSitter.DAL.Repositories;
using Microsoft.Extensions.Logging;

namespace DogSitter.BLL.Services
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _rep;
        private readonly IMapper _map;
        private readonly ISitterRepository _sitterRepository;
        private readonly IUserRepository _userRepository;
        private IWorkTimeRepository _workTimeRepository;
        private readonly IDogRepository _dogRepository;
        private readonly IServiceRepository _serviceRepository;
        private readonly ILogger<EmailSendller> _logger;
        private readonly IAdminRepository _adminRepository;

        public OrderService(IOrderRepository orderRepository, ILogger<EmailSendller> logger, IAdminRepository adminRepository,
            ISitterRepository sitterRepository, IMapper mapper, IUserRepository userRepository, IWorkTimeRepository workTimeRepository, IDogRepository dogRepository, IServiceRepository serviceRepository)
        {
            _rep = orderRepository;
            _sitterRepository = sitterRepository;
            _adminRepository = adminRepository;
            _map = mapper;
            _userRepository = userRepository;
            _workTimeRepository = workTimeRepository;
            _dogRepository = dogRepository;
            _serviceRepository = serviceRepository;
            _logger = logger;
        }

        public int Add(int userId, OrderModel orderModel)
        {
            orderModel.Price = GetOrderTotalSum(orderModel);
            var customer = (Customer)_userRepository.GetUserById(userId);
            var orderId = _rep.Add(_map.Map<Order>(orderModel), customer);

            EmailSendller emailSendller = new EmailSendller(_logger);
            emailSendller.SendMessage(orderModel.Sitter, EmailMessage.NewOrderForSitter(orderId), EmailTopic.NewOrder);

            return orderId;
        }

        public void Update(int userId, OrderModel orderModel)
        {
            var user = _userRepository.GetUserById(userId);
            if (user.Role != Role.Customer)
            {
                throw new AccessException("Not enough rights");
            }

            var order = _rep.GetById(orderModel.Id);
            if (order == null)
            {
                throw new EntityNotFoundException($"Order {orderModel.Id} was not found");
            }

            order.OrderDate = orderModel.OrderDate;
            order.Sitter = _sitterRepository.GetById(orderModel.Sitter.Id);
            order.Dog = _dogRepository.GetDogById(orderModel.Dog.Id);
            if (order.Service != null)
            {
                order.Service.Clear();
            }
            else
            {
                order.Service = new List<DAL.Entity.Serviсe>();
            }
            foreach (var item in orderModel.Services)
            {
                order.Service.Add(_serviceRepository.GetServiceById(item.Id));
            }

            if (order.SitterWorkTime != null)
            {
                var orderOldWorkTime = _workTimeRepository.GetWorkTimeById(order.SitterWorkTime.Id);
                _workTimeRepository.ChangeWorkTimeStatus(orderOldWorkTime, false);
            }
            if (orderModel.SitterWorkTime != null)
            {

                var workTime = _workTimeRepository.GetWorkTimeById(orderModel.SitterWorkTime.Id);

                if (workTime == null)
                {
                    throw new WorkTimeBusyException("This worktime is busy");
                }

                _workTimeRepository.ChangeWorkTimeStatus(workTime, true);
                order.SitterWorkTime = workTime;

            }
            if (order.Status == Status.Created)
            {
                _rep.Update(order);
                EmailSendller emailSendller = new EmailSendller(_logger);
                emailSendller.SendMessage(orderModel.Sitter, EmailMessage.UpdateOrderForSitter(order.Id), EmailTopic.UpdateOrder);
            }
            else
            {
                throw new Exception($"Order { orderModel.Id } has been accepted, it cannot be edited");
            }
        }

        public void EditOrderStatusByOrderId(int userId, int id, int status)
        {
            var order = _rep.GetById(id);
            if (order == null)
            {
                throw new EntityNotFoundException($"Order {id} was not found");
            }
            var user = _userRepository.GetUserById(userId);
            if ((user.Role == Role.Customer && (Status)status != Status.CancelledByCustomer) ||
                (user.Role == Role.Sitter && (Status)status == Status.CanceledByAdmin))
            {
                throw new AccessException("Not enough rights");
            }
            _rep.EditOrderStatusByOrderId(order, status);
            EmailSendller emailSendller = new EmailSendller(_logger);
            emailSendller.SendMessage(_map.Map<SitterModel>(order.Sitter), EmailMessage.NewOrderStatus(order.Id, (Status)status), EmailTopic.UpdateOrder);
            emailSendller.SendMessage(_map.Map<CustomerModel>(order.Customer), EmailMessage.NewOrderStatus(order.Id, (Status)status), EmailTopic.UpdateOrder);
        }

        public List<OrderModel> GetAllOrdersBySitterId(int userId, int id)
        {
            var sitter = _userRepository.GetUserById(id);
            if (sitter == null || sitter.Role != Role.Sitter)
            {
                throw new EntityNotFoundException($"Customer {id} was not found");
            }
            if (_userRepository.GetUserById(userId).Role != Role.Admin && userId != id)
            {
                throw new AccessException("Not enough rights");
            }
            return _map.Map<List<OrderModel>>(_rep.GetAllOrdersBySitterId(id));
        }

        public List<OrderModel> GetAllOrdersByCustomerId(int userId, int id)
        {
            var customer = _userRepository.GetUserById(userId);
            if (customer == null || customer.Role != Role.Customer)
            {
                throw new EntityNotFoundException($"Customer {id} was not found");
            }

            return _map.Map<List<OrderModel>>(_rep.GetAllOrdersByCustomerId(id));
        }

        public void AddCommentAndMarkAboutOrder(int id, OrderModel order)
        {
            var entity = _rep.GetById(id);
            var sitter = _sitterRepository.GetById(entity.Sitter.Id);

            if (entity == null)
            {
                throw new EntityNotFoundException($"Order was not found");
            }
            _rep.LeaveCommentAndRateOrder(entity, _map.Map<Order>(order));

            EmailSendller emailSendller = new EmailSendller(_logger);
            emailSendller.SendMessage(_map.Map<SitterModel>(order.Sitter), EmailMessage.NewComment(order.Id), EmailTopic.UpdateOrder);

            var admins = _adminRepository.GetAllAdminWithContacts();
            var adminsModel = _map.Map<List<AdminModel>>(admins);

            foreach (var a in adminsModel)
            {
                emailSendller.SendMessage(a, EmailMessage.NewComment(order.Id), EmailTopic.NewCommentForAdmin);
            }

            var orders = _sitterRepository.GetAllSitterOrders(sitter);
            double SitterNewRating = 0;
            foreach (var item in orders)
            {
                SitterNewRating += item.Mark.Value;
            }
            if (orders.Count > 0)
            {
                SitterNewRating /= orders.Count;
            }
            if (sitter.Rating != SitterNewRating)
            {
                sitter.Rating = SitterNewRating;
                _sitterRepository.ChangeRating(sitter);
                emailSendller.SendMessage(_map.Map<SitterModel>(order.Sitter), EmailMessage.UpdateRatingSitter(sitter.Rating, SitterNewRating), EmailTopic.UpdateRating);
            }
        }

        private decimal GetOrderTotalSum(OrderModel orderModel)
        {
            if (orderModel.Services == null)
            {
                return 0;
            }
            return orderModel.Services.Select(s => s.Price).Sum();
        }

        public OrderModel GetOrderById(int id)
        {
            var order = _rep.GetById(id);
            if (order == null)
            {
                throw new EntityNotFoundException("Order not found");
            }
            return _map.Map<OrderModel>(order);
        }
    }
}
