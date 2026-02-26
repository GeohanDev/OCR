import { useQuery } from '@tanstack/react-query';
import { dashboardApi } from '../api/client';
import StatusBadge from '../components/ui/StatusBadge';
import { FileText, Clock, CheckCircle, Send } from 'lucide-react';
import type { DashboardKpis } from '../types';
import { Link } from 'react-router-dom';

function KpiCard({ label, value, icon: Icon, color }: {
  label: string; value: number; icon: React.ElementType; color: string;
}) {
  return (
    <div className="card p-5 flex items-center gap-4">
      <div className={`p-3 rounded-lg ${color}`}>
        <Icon className="h-6 w-6 text-white" />
      </div>
      <div>
        <p className="text-sm text-gray-500">{label}</p>
        <p className="text-2xl font-bold text-gray-900">{value}</p>
      </div>
    </div>
  );
}

export default function DashboardPage() {
  const { data, isLoading, isError } = useQuery<DashboardKpis>({
    queryKey: ['dashboard-kpis'],
    queryFn: () => dashboardApi.getKpis().then(r => r.data),
    refetchInterval: 30000,
  });

  if (isLoading) return <div className="text-center py-12 text-gray-500">Loading dashboard...</div>;
  if (isError || !data) return <div className="text-center py-12 text-gray-400">Could not load dashboard data.</div>;

  const kpis = data;

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold text-gray-900">Dashboard</h1>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <KpiCard label="Total Documents" value={kpis.totalDocuments} icon={FileText} color="bg-gray-500" />
        <KpiCard label="Pending Review" value={kpis.pendingReview} icon={Clock} color="bg-orange-500" />
        <KpiCard label="Approved" value={kpis.approved} icon={CheckCircle} color="bg-green-500" />
        <KpiCard label="Pushed to ERP" value={kpis.pushedToErp} icon={Send} color="bg-purple-500" />
      </div>

      <div className="card">
        <div className="p-4 border-b border-gray-100">
          <h2 className="font-semibold text-gray-900">Recent Documents</h2>
        </div>
        <div className="divide-y divide-gray-50">
          {kpis.recentDocuments.length === 0 && (
            <p className="p-4 text-sm text-gray-500">No documents yet.</p>
          )}
          {kpis.recentDocuments.map((doc) => (
            <Link
              key={doc.id}
              to={`/documents/${doc.id}`}
              className="flex items-center justify-between p-4 hover:bg-gray-50 transition-colors"
            >
              <div>
                <p className="text-sm font-medium text-gray-900 truncate max-w-xs">{doc.originalFilename}</p>
                <p className="text-xs text-gray-500">
                  {doc.uploadedByUsername} · {new Date(doc.uploadedAt).toLocaleDateString()}
                </p>
              </div>
              <StatusBadge status={doc.status} />
            </Link>
          ))}
        </div>
      </div>
    </div>
  );
}
