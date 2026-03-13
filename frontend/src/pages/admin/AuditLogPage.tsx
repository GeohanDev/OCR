import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { usersApi } from '../../api/client';
import type { AuditLog, PagedResult } from '../../types';
import { ChevronLeft, ChevronRight, Search } from 'lucide-react';

const EVENT_TYPES = ['', 'Upload', 'Correction', 'Approval', 'Rejection', 'UserRefresh', 'Push', 'ConfigChange', 'StatusChange'];

export default function AuditLogPage() {
  const [page, setPage] = useState(1);
  const [eventType, setEventType] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');

  const { data, isLoading } = useQuery<PagedResult<AuditLog>>({
    queryKey: ['audit-logs', page, eventType, search],
    queryFn: () => usersApi.getAuditLogs({ page, pageSize: 50, eventType: eventType || undefined, search: search || undefined }).then(r => r.data),
  });

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold text-gray-900">Audit Log</h1>

      <div className="flex flex-col sm:flex-row gap-3">
        <div className="relative flex-1">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-gray-400" />
          <input
            className="input pl-9"
            placeholder="Search by user or entity..."
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') { setSearch(searchInput); setPage(1); } }}
          />
        </div>
        <select
          className="input w-auto"
          value={eventType}
          onChange={e => { setEventType(e.target.value); setPage(1); }}
        >
          {EVENT_TYPES.map(t => <option key={t} value={t}>{t || 'All events'}</option>)}
        </select>
      </div>

      <div className="card overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 border-b border-gray-200">
            <tr>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Time</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Event</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Actor</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Target</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {isLoading && (
              <tr><td colSpan={4} className="px-4 py-8 text-center text-gray-500">Loading...</td></tr>
            )}
            {data?.items.map(log => (
              <tr key={log.id} className="hover:bg-gray-50">
                <td className="px-4 py-3 text-gray-500 whitespace-nowrap text-xs">
                  {new Date(log.occurredAt).toLocaleString()}
                </td>
                <td className="px-4 py-3">
                  <span className={`badge text-xs ${eventTypeBadge(log.eventType)}`}>{log.eventType}</span>
                </td>
                <td className="px-4 py-3 text-gray-600">{log.actorUsername ?? '—'}</td>
                <td className="px-4 py-3 text-gray-600 text-xs">
                  {log.targetEntityType && <span className="font-medium">{log.targetEntityType}</span>}
                  {log.targetEntityId && <span className="ml-1 font-mono">{log.targetEntityId.slice(0, 8)}...</span>}
                </td>
              </tr>
            ))}
            {!isLoading && data?.items.length === 0 && (
              <tr><td colSpan={4} className="px-4 py-8 text-center text-gray-500">No audit events found.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {data && data.totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-gray-600">
            Showing {(page - 1) * 50 + 1}–{Math.min(page * 50, data.totalCount)} of {data.totalCount}
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

function eventTypeBadge(type: string): string {
  const map: Record<string, string> = {
    Upload: 'bg-blue-100 text-blue-700',
    Approval: 'bg-green-100 text-green-700',
    Rejection: 'bg-red-100 text-red-700',
    Push: 'bg-purple-100 text-purple-700',
    UserRefresh: 'bg-orange-100 text-orange-700',
    ConfigChange: 'bg-yellow-100 text-yellow-700',
  };
  return map[type] ?? 'bg-gray-100 text-gray-700';
}
