import axios from 'axios';

const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5000/api';

// Create custom Axios instance
const apiClient = axios.create({
  baseURL: API_BASE_URL,
  headers: {
    'Content-Type': 'application/json',
  },
});

// Variable to hold active tenant
let activeTenant = null;

export const setActiveTenantContext = (tenant) => {
  activeTenant = tenant;
  if (tenant && tenant.id) {
    apiClient.defaults.headers.common['X-Tenant-ID'] = tenant.id;
    apiClient.defaults.headers.common['X-Tenant-Code'] = tenant.code || tenant.id;
  } else {
    delete apiClient.defaults.headers.common['X-Tenant-ID'];
    delete apiClient.defaults.headers.common['X-Tenant-Code'];
  }
};

export const getActiveTenantContext = () => activeTenant;

// Request Interceptor: Attach Tenant Header dynamically
apiClient.interceptors.request.use((config) => {
  if (activeTenant && activeTenant.id) {
    config.headers['X-Tenant-ID'] = activeTenant.id;
    config.headers['X-Tenant-Code'] = activeTenant.code || activeTenant.id;
  }
  return config;
}, (error) => Promise.reject(error));

// API Services
export const TenantApi = {
  getAll: async () => {
    const res = await apiClient.get('/tenants');
    return res.data;
  },
  getById: async (id) => {
    const res = await apiClient.get(`/tenants/${id}`);
    return res.data;
  },
  create: async (data) => {
    const res = await apiClient.post('/tenants', data);
    return res.data;
  },
  getMetrics: async () => {
    const res = await apiClient.get('/tenants/current/metrics');
    return res.data;
  }
};

export const InventoryApi = {
  getAll: async (params = {}) => {
    const res = await apiClient.get('/inventory', { params });
    return res.data;
  },
  getById: async (id) => {
    const res = await apiClient.get(`/inventory/${id}`);
    return res.data;
  },
  create: async (data) => {
    const res = await apiClient.post('/inventory', data);
    return res.data;
  },
  update: async (id, data) => {
    const res = await apiClient.put(`/inventory/${id}`, data);
    return res.data;
  },
  delete: async (id) => {
    const res = await apiClient.delete(`/inventory/${id}`);
    return res.data;
  },
  adjustStock: async (id, data) => {
    const res = await apiClient.post(`/inventory/${id}/adjust-stock`, data);
    return res.data;
  },
  uploadDocument: async (id, file) => {
    const formData = new FormData();
    formData.append('file', file);
    const res = await apiClient.post(`/inventory/${id}/upload-s3`, formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return res.data;
  },
  getTransactions: async (id) => {
    const res = await apiClient.get(`/inventory/${id}/transactions`);
    return res.data;
  }
};

export const StorageApi = {
  getTenantFiles: async () => {
    const res = await apiClient.get('/storage/files');
    return res.data;
  },
  getPresignedDownloadUrl: async (fileKey) => {
    const res = await apiClient.get('/storage/presigned-url', { params: { fileKey } });
    return res.data;
  }
};

export const SecurityAuditApi = {
  probeCrossTenantAccess: async (targetTenantId, itemId) => {
    const res = await apiClient.post('/audit/probe-isolation', {
      targetTenantId,
      itemId
    });
    return res.data;
  },
  getAuditLogs: async () => {
    const res = await apiClient.get('/audit/logs');
    return res.data;
  }
};

export default apiClient;
