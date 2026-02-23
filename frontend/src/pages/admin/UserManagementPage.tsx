import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { usersApi } from '../../api/client';
import { useAuth } from '../../contexts/AuthContext';
import type { User, PagedResult } from '../../types';
import { RefreshCw, Loader2, ChevronLeft, ChevronRight } from 'lucide-react';

const ROLES = ['Normal', 'Manager', 'Admin'] as const;

export default function UserManagementPage() {
  const queryClient = useQueryClient();
  const { isAdmin } = useAuth();
  const [page, setPage] = useState(1);
  const [syncResult, setSyncResult] = useState<string | null>(null);

  const { data, isLoading } = useQuery<PagedResult<User>>({
    queryKey: ['users', page],
    queryFn: () => usersApi.list({ page, pageSize: 20 }).then(r => r.data),
  });

  const sync = useMutation({
    mutationFn: () => usersApi.sync(),
    onSuccess: (res) => {
      const { created, updated, deactivated } = res.data;
      setSyncResult(`Sync complete: ${created} created, ${updated} updated, ${deactivated} deactivated`);
      queryClient.invalidateQueries({ queryKey: ['users'] });
      setTimeout(() => setSyncResult(null), 5000);
    },
  });

  const updateRole = useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: string }) =>
      usersApi.updateRole(userId, role),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
  });

  const toggleActive = useMutation({
    mutationFn: ({ userId, isActive }: { userId: string; isActive: boolean }) =>
      usersApi.setActive(userId, isActive),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
  });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-gray-900">User Management</h1>
        <button
          onClick={() => sync.mutate()}
          disabled={sync.isPending}
          className="btn-secondary flex items-center gap-2"
        >
          {sync.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
          Sync from Acumatica
        </button>
      </div>

      {syncResult && (
        <div className="bg-green-50 border border-green-200 rounded-lg p-3 text-sm text-green-700">
          {syncResult}
        </div>
      )}

      <div className="card overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 border-b border-gray-200">
            <tr>
              <th className="px-4 py-3 text-left font-medium text-gray-500">User</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Username</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Branch</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Role</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Status</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {isLoading && (
              <tr><td colSpan={5} className="px-4 py-8 text-center text-gray-500">Loading...</td></tr>
            )}
            {data?.items.map(user => (
              <tr key={user.id} className="hover:bg-gray-50">
                <td className="px-4 py-3">
                  <p className="font-medium text-gray-900">{user.displayName}</p>
                  <p className="text-xs text-gray-500">{user.email}</p>
                </td>
                <td className="px-4 py-3 text-gray-600 font-mono text-xs">{user.username}</td>
                <td className="px-4 py-3 text-gray-600">{user.branchName ?? '—'}</td>
                <td className="px-4 py-3">
                  {isAdmin ? (
                    <select
                      className="input py-1 text-sm w-auto"
                      value={user.role}
                      onChange={e => updateRole.mutate({ userId: user.id, role: e.target.value })}
                    >
                      {ROLES.map(r => <option key={r} value={r}>{r}</option>)}
                    </select>
                  ) : (
                    <span className={`badge ${
                      user.role === 'Admin' ? 'bg-purple-100 text-purple-700' :
                      user.role === 'Manager' ? 'bg-blue-100 text-blue-700' :
                      'bg-gray-100 text-gray-600'
                    }`}>{user.role}</span>
                  )}
                </td>
                <td className="px-4 py-3">
                  {isAdmin ? (
                    <button
                      onClick={() => toggleActive.mutate({ userId: user.id, isActive: !user.isActive })}
                      className={`badge cursor-pointer ${user.isActive ? 'bg-green-100 text-green-700' : 'bg-red-100 text-red-700'}`}
                    >
                      {user.isActive ? 'Active' : 'Inactive'}
                    </button>
                  ) : (
                    <span className={`badge ${user.isActive ? 'bg-green-100 text-green-700' : 'bg-red-100 text-red-700'}`}>
                      {user.isActive ? 'Active' : 'Inactive'}
                    </span>
                  )}
                </td>
              </tr>
            ))}
            {!isLoading && data?.items.length === 0 && (
              <tr><td colSpan={5} className="px-4 py-8 text-center text-gray-500">No users found.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {data && data.totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-gray-600">
            {data.totalCount} users total
          </p>
          <div className="flex gap-2">
            <button className="btn-secondary p-2" disabled={!data.hasPreviousPage} onClick={() => setPage(p => p - 1)}>
              <ChevronLeft className="h-4 w-4" />
            </button>
            <button className="btn-secondary p-2" disabled={!data.hasNextPage} onClick={() => setPage(p => p + 1)}>
              <ChevronRight className="h-4 w-4" />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
