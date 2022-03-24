﻿using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using TodoWeb.Dtos;
using TodoWeb.Extensions;
using TodoWeb.Models;

namespace TodoWeb.Data.Services
{
    public class TodoListService : ITodoListService
    {
        private readonly ApplicationDbContext _context;
        private readonly ITodoService _todoService;
        private readonly CommandResult _commandResult;
        private readonly IAccountService _accountService;
        private readonly int titleCharLimit = 50;
        private readonly int descriptionCharLimit = 300;
        public TodoListService(ApplicationDbContext context,
            IAccountService accountService, 
            ITodoService todoService)
        {
            _context = context;
            _commandResult = new();
            _accountService = accountService;
            _todoService = todoService;
        }
        public async Task<CommandResult> CreateAsync(CreateTodoListArgs args)
        {
            if (!ValidateCreateArgs(args))
            {
                return _commandResult;
            }
            User? user = await _accountService.GetCurrentUser();
            if (user == null)
            {
                _commandResult.AddError("User", "Please log in.");
                return _commandResult;
            }
            if (await HasDuplicateByTitleAsync(args.Title.Trim()))
            {
                _commandResult.AddError("Title", "You already have a list with this title.");
                return _commandResult;
            }

            TodoList todoList = new()
            {
                CreatedBy = user,
                Title = args.Title.Trim(),
                Description = args.Description?.Trim(),
            };
            await _context.TodoLists.AddAsync(todoList);
            await _context.SaveChangesAsync();
            return _commandResult;
        }

        public async Task<CommandResult> DeleteAsync(int id)
        {
            TodoList? todoList = await _context.TodoLists
                .Include(tl => tl.Todos)
                .Include(tl => tl.CoauthorUsers)
                .FirstOrDefaultAsync(tl => tl.Id == id);

            if (!await HasPermissionAsync(todoList))
            {
                _commandResult.AddError("User", "User does not have permission to delete this list.");
                return _commandResult;
            }

            if (todoList != null)
            {
                foreach (var coauthorship in todoList.CoauthorUsers)
                {
                    _context.TodoListCoauthorships.Remove(coauthorship);
                }
                _context.TodoLists.Remove(todoList);
                await _context.SaveChangesAsync();
                return _commandResult;
            } 
            _commandResult.AddError("Todo", "The task specified by the ID does not exist.");
            return _commandResult;
        }

        public async Task<IEnumerable<TodoListViewDto>> GetAllAsync()
        {
            User? currentUser = await _accountService.GetCurrentUser();
            var todoLists = await _context.TodoLists
                .Include(list => list.CreatedBy)
                .Include(list => list.Todos)
                .Include(list => list.CoauthorUsers)
                .Where(list => list.CreatedBy == currentUser)
                .ToListAsync();

            return todoLists.Select(tl => tl.GetViewDto());
        }

        public async Task<IEnumerable<TodoListViewDto>> GetUserCoauthoredLists()
        {
            User? user = await _accountService.GetCurrentUser();
            if (user == null)
            {
                return new List<TodoListViewDto>();
            }
            return user.CoauthoredLists
                .Where(coauthorship => coauthorship.ListId != null)
                .Select(coauthorship => {
                    return _context.TodoLists
                    .Include(tl => tl.CreatedBy)
                    .First(tl => tl.Id == coauthorship.ListId)
                    .GetViewDto();
                });
        }

        public async Task<TodoListViewDto?> GetByIdAsync(int id)
        {
            var todoList = await _context.TodoLists
                .Include(list => list.CreatedBy)
                .Include(list => list.Todos)
                .Include(list => list.CoauthorUsers)
                .Include("CoauthorUsers.User")
                .FirstOrDefaultAsync(list => list.Id == id);

            return await HasPermissionAsync(todoList) 
                ? (todoList?.GetViewDto())
                : null;
        }
        public async Task<bool> HasPermissionAsync(TodoList? todoList)
        {
            User? currentUser = await _accountService.GetCurrentUser();
            if (todoList == null)
            {
                return false;
            }
            if (currentUser != null && 
                currentUser.Roles.Any(role => role.Name == "Admin"))
            {
                return true;
            }

            bool isCoauthor = todoList.CoauthorUsers
                .Any(c => c.UserId == currentUser?.Id);
            bool isAuthor = todoList.CreatedBy.Id == currentUser?.Id;

            return isAuthor || isCoauthor;
        }
        public async Task<CommandResult> UpdateAsync(UpdateTodoListArgs args)
        {
            var todoList = await _context.TodoLists
                .Include(tl => tl.CoauthorUsers)
                .Include(tl => tl.CreatedBy)
                .FirstOrDefaultAsync(t => t.Id == args.Id);
            if (todoList == null)
            {
                _commandResult.AddError("TodoLists", $"List with ID {args.Id} not found.");
                return _commandResult;
            } 

            if (!await HasPermissionAsync(todoList))
            {
                _commandResult.AddError("User", "User does not have permission to update this list.");
                return _commandResult;
            }
            
            args.Title = args.Title.Trim();
            args.Description = args.Description?.Trim();

            if (await HasDuplicateByTitleAsync(args.Title, todoList.CreatedBy))
            {
                _commandResult.AddError("Title", "A task with this title already exists.");
                return _commandResult;
            }                     
            try
            {
                CreateTodoListArgs createArgs = new()
                {
                    Title = args.Title,
                    Description = args.Description
                };
                if (ValidateCreateArgs(createArgs))
                {
                    todoList.Description = (args.Description ?? "").Trim();
                    todoList.Title = args.Title.Trim();
                    _context.TodoLists.Update(todoList);
                    await _context.SaveChangesAsync();
                }
            }
            catch
            {
                _commandResult.AddError("Todos", "There was an error creating your task.");
            }
            return _commandResult;
        }
        public async Task<bool> HasDuplicateByTitleAsync(string title, User? creator = null)
        {
            if (creator == null)
            {
                creator = await _accountService.GetCurrentUser();
            }
            return await _context.TodoLists
                .Include(list => list.CreatedBy)
                .Include(list => list.Todos)
                .Where(list => list.CreatedBy == creator)
                .AnyAsync(list => list.Title == title);
        }
        public bool ValidateCreateArgs(CreateTodoListArgs args)
        {
            string title = args.Title;
            if (title.Trim().Length >=  titleCharLimit)
            {
                _commandResult.AddError("Title", "The inputted title is too long.");
            }
            else if (Regex.Match(title, @"[^a-zA-Z0-9\-_\s()]").Success)
            {
                _commandResult.AddError("Title", "Special characters are not allowed.");
            }

            if (args.Description != null && args.Description.Length > descriptionCharLimit)
            {
                _commandResult.AddError("Description", "The given description is too long.");
            }
            return _commandResult.IsValid;
        }

        public async Task<IEnumerable<UserViewDto>> GetNonCoauthors(int id)
        {
            TodoList? todoList = await _context.TodoLists
                .Include(tl => tl.CoauthorUsers)
                .FirstOrDefaultAsync(tl => tl.Id == id);

            if (todoList == null)
            {
                return new List<UserViewDto>();
            }

            var coauthorIds = todoList.CoauthorUsers
                .Select(coauthor => coauthor.UserId);

            User? currentUser = await _accountService.GetCurrentUser();
            if (currentUser != null)
            {
                coauthorIds = coauthorIds.Append(currentUser.Id);
            }

            var nonCoauthors = await _context.Users
                .Where(user => !coauthorIds.Contains(user.Id))
                .Select(user => user.GetViewDto())
                .ToListAsync();

            return nonCoauthors;
        }

        public async Task<CommandResult> AddPermission(int id, int coauthorId)
        {
            TodoList? todoList = await _context.TodoLists.FindAsync(id);
            if (todoList == null)
            {
                _commandResult.AddError("TodoLists", $"Todo list {id} was not found.");
                return _commandResult;
            }
            User? coauthor = await _context.Users.FindAsync(coauthorId);
            if (coauthor == null)
            {
                _commandResult.AddError("Users", $"User {coauthorId} was not found.");
                return _commandResult;
            }

            await _context.TodoListCoauthorships.AddAsync(new()
            {
                ListId = id,
                UserId = coauthorId
            });
            await _context.SaveChangesAsync();
            return _commandResult;
        }

        public async Task<CommandResult> RemovePermission(int id, int coauthorId)
        {
            var coauthorship = await _context.TodoListCoauthorships
                .FirstOrDefaultAsync(c => c.UserId == coauthorId && c.ListId == id);
            if (coauthorship == null)
            {
                _commandResult.AddError("TodoListPermissions", $"Permission for user {coauthorId} on list {id} not found.");
                return _commandResult;
            }
            _context.TodoListCoauthorships.Remove(coauthorship);
            await _context.SaveChangesAsync();
            return _commandResult;
        }
    }
}
